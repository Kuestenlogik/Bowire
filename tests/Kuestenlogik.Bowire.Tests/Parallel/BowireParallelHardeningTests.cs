// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Parallel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Parallel;

/// <summary>
/// The #313 hardening of distributed parallel runs: an executor's target
/// allowlist, its token, a coordinator that will not send in clear, and the
/// chained audit log both sides write.
/// </summary>
/// <remarks>
/// An executor listens beyond loopback — that is what makes it an executor —
/// so without these it will load-test whatever any caller names. The tests
/// hold that a refused job reaches no target at all, not that it merely
/// fails.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class BowireParallelHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bowire-par-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 192.0.2.0/24 is TEST-NET-1: nothing answers there, and nothing may be tried.
    private static readonly string[] TestNetExecutor = ["http://192.0.2.10:5080"];

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    private async Task<(WebApplication App, HttpClient Http, BowireParallelAuditLog Audit)> Host(params (string Key, string Value)[] settings)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        b.Configuration.AddInMemoryCollection(settings.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)));
        var audit = new BowireParallelAuditLog(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        b.Services.AddSingleton(audit);
        var app = b.Build();
        app.MapBowireParallelEndpoints("");
        await app.StartAsync(Ct);
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, audit);
    }

    private sealed class Counter { public int Hits; }

    private static async Task<(WebApplication App, string Url, Counter Counter, List<string> Seen)> Upstream()
    {
        var counter = new Counter();
        var seen = new List<string>();
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = b.Build();
        app.Run(async ctx =>
        {
            Interlocked.Increment(ref counter.Hits);
            lock (seen)
            {
                seen.Add($"{ctx.Request.Path}|{ctx.Request.Headers.Authorization}|{ctx.Request.Headers[BowireParallelEndpoints.JobHeader]}");
            }
            if (ctx.Request.Path.StartsWithSegments("/api/parallel/start-local"))
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("""{"sessionCount":1,"targetCount":1,"totalDurationMs":1,"passCount":1,"failCount":0,"sessions":[],"results":[]}""", Ct);
                return;
            }
            await ctx.Response.WriteAsync("ok", Ct);
        });
        await app.StartAsync(Ct);
        return (app, app.Urls.First(), counter, seen);
    }

    private static object Job(string target, int sessions = 1) => new
    {
        targets = new[] { new { url = target, method = "GET" } },
        sessionCount = sessions,
    };

    private static List<JsonElement> Lines(BowireParallelAuditLog audit) =>
        File.Exists(audit.File)
            ? [.. File.ReadAllLines(audit.File).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone())]
            : [];

    // ---- the policy ----

    [Theory]
    [InlineData("https://api.staging.example/*", "https://api.staging.example/orders", true)]
    [InlineData("https://api.staging.example/*", "https://API.staging.example/orders", true)]
    [InlineData("https://api.staging.example/*", "https://api.staging.example.evil/orders", false)]
    [InlineData("https://api.staging.example/*", "http://api.staging.example/orders", false)]
    [InlineData("http://127.0.0.1:*/*", "http://127.0.0.1:5000/x", true)]
    public void An_Allowlist_Pattern_Covers_The_Whole_Url(string pattern, string url, bool allowed)
    {
        var policy = BowireParallelPolicy.From(Config(("Bowire:Parallel:TargetAllowlist", pattern)));
        Assert.Equal(allowed, policy.RefusedTargets([url]).Count == 0);
    }

    [Fact]
    public void The_Allowlist_Is_A_List_Or_One_Separated_String()
    {
        var list = BowireParallelPolicy.From(Config(
            ("Bowire:Parallel:TargetAllowlist:0", "https://a.example/*"),
            ("Bowire:Parallel:TargetAllowlist:1", "https://b.example/*")));
        var joined = BowireParallelPolicy.From(Config(("Bowire:Parallel:TargetAllowlist", "https://a.example/*; https://b.example/*")));
        Assert.Equal(list.TargetAllowlist.Order(), joined.TargetAllowlist.Order());
        Assert.Empty(joined.RefusedTargets(["https://b.example/x"]));
    }

    [Fact]
    public void Without_An_Allowlist_Every_Target_Is_Accepted_As_Before()
    {
        Assert.Empty(BowireParallelPolicy.From(Config()).RefusedTargets(["https://anything.example/"]));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("Bearer wrong", false)]
    [InlineData("Basic s3cret", false)]
    [InlineData("Bearer s3cret", true)]
    [InlineData("bearer s3cret", true)]
    public void A_Configured_Token_Is_Required_Exactly(string? header, bool ok)
    {
        var policy = BowireParallelPolicy.From(Config(("Bowire:Parallel:Token", "s3cret")));
        Assert.Equal(ok, policy.Authorizes(header));
    }

    [Theory]
    [InlineData("http://10.0.0.5:5080", true)]
    [InlineData("https://node-eu.example", false)]
    [InlineData("http://localhost:5080", false)]
    [InlineData("http://127.0.0.1:5080", false)]
    public void RequireSignedExecutor_Refuses_Clear_Text_Beyond_Loopback(string executor, bool refused)
    {
        var policy = BowireParallelPolicy.From(Config(("Bowire:Parallel:RequireSignedExecutor", "true")));
        Assert.Equal(refused, policy.ExecutorRefusal(executor) is not null);
        Assert.Null(BowireParallelPolicy.From(Config()).ExecutorRefusal(executor));
    }

    // ---- the audit chain ----

    [Fact]
    public void An_Intact_Chain_Verifies_Across_Instances()
    {
        var root = Path.Combine(_root, "chain");
        new BowireParallelAuditLog(root).Record("run", new { job = "a" });
        new BowireParallelAuditLog(root).Record("run", new { job = "b" });
        var log = new BowireParallelAuditLog(root);
        log.Record("dispatch", new { job = "c" });
        Assert.Null(BowireParallelAuditLog.FirstBrokenLine(log.File));
        Assert.Equal(BowireParallelAuditLog.Genesis, Lines(log)[0].GetProperty("prevHash").GetString());
    }

    [Fact]
    public void A_Line_Taken_Out_Of_The_Middle_Breaks_The_Chain_Where_It_Was()
    {
        var log = new BowireParallelAuditLog(Path.Combine(_root, "snip"));
        for (var i = 0; i < 4; i++) log.Record("run", new { n = i });
        var lines = File.ReadAllLines(log.File).ToList();
        lines.RemoveAt(1);
        File.WriteAllLines(log.File, lines);
        Assert.Equal(2, BowireParallelAuditLog.FirstBrokenLine(log.File));
    }

    [Fact]
    public void An_Edited_Line_Breaks_The_Chain_At_The_Next_One()
    {
        var log = new BowireParallelAuditLog(Path.Combine(_root, "edit"));
        for (var i = 0; i < 3; i++) log.Record("run", new { pass = 10 });
        var lines = File.ReadAllLines(log.File);
        lines[0] = lines[0].Replace("\"pass\":10", "\"pass\":99", StringComparison.Ordinal);
        File.WriteAllLines(log.File, lines);
        Assert.Equal(2, BowireParallelAuditLog.FirstBrokenLine(log.File));
    }

    // ---- the executor ----

    [Fact]
    public async Task A_Target_Outside_The_Allowlist_Is_403_And_Never_Contacted()
    {
        var (up, upUrl, counter, _) = await Upstream();
        await using var _u = up;
        var (app, http, audit) = await Host(("Bowire:Parallel:TargetAllowlist", "https://only.this.example/*"));
        await using var _a = app;

        var resp = await http.PostAsJsonAsync("/api/parallel/start-local", Job(upUrl + "/x", sessions: 5), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains(upUrl, await resp.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(0, counter.Hits);
        var refused = Assert.Single(Lines(audit));
        Assert.Equal("refused", refused.GetProperty("kind").GetString());
        Assert.Equal(upUrl + "/x", refused.GetProperty("target").GetString());
    }

    [Fact]
    public async Task A_Covered_Target_Runs_And_The_Run_Is_Audited()
    {
        var (up, upUrl, counter, _) = await Upstream();
        await using var _u = up;
        var (app, http, audit) = await Host(("Bowire:Parallel:TargetAllowlist", "http://127.0.0.1:*/*"));
        await using var _a = app;

        // Sessions share the targets round-robin (session k takes the
        // targets whose index % sessions == k), so two targets for two.
        var resp = await http.PostAsJsonAsync("/api/parallel/start-local", new
        {
            targets = new[] { new { url = upUrl + "/x", method = "GET" }, new { url = upUrl + "/y", method = "GET" } },
            sessionCount = 2,
        }, Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, counter.Hits);
        var run = Assert.Single(Lines(audit));
        Assert.Equal("run", run.GetProperty("kind").GetString());
        Assert.Equal(2, run.GetProperty("pass").GetInt32());
    }

    [Fact]
    public async Task With_A_Token_Configured_A_Caller_Without_It_Is_401_Before_Anything_Runs()
    {
        var (up, upUrl, counter, _) = await Upstream();
        await using var _u = up;
        var (app, http, audit) = await Host(("Bowire:Parallel:Token", "s3cret"));
        await using var _a = app;

        var anonymous = await http.PostAsJsonAsync("/api/parallel/start-local", Job(upUrl + "/x"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, counter.Hits);
        Assert.Equal("unauthorized", Assert.Single(Lines(audit)).GetProperty("kind").GetString());

        using var withToken = new HttpRequestMessage(HttpMethod.Post, "/api/parallel/start-local") { Content = JsonContent.Create(Job(upUrl + "/x")) };
        withToken.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "s3cret");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(withToken, Ct)).StatusCode);
        Assert.Equal(1, counter.Hits);
    }

    // ---- the coordinator ----

    [Fact]
    public async Task The_Coordinator_Sends_The_Configured_Token_And_A_Job_Id_Both_Logs_Share()
    {
        var (executor, executorUrl, _, seen) = await Upstream();
        await using var _e = executor;
        var (app, http, audit) = await Host(("Bowire:Parallel:Token", "s3cret"));
        await using var _a = app;

        var resp = await http.PostAsJsonAsync("/api/parallel/start", new
        {
            targets = new[] { new { url = "http://127.0.0.1:9/x", method = "GET" } },
            sessionCount = 1,
            hosts = new[] { executorUrl },
        }, Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var call = Assert.Single(seen).Split('|');
        Assert.Equal("/api/parallel/start-local", call[0]);
        Assert.Equal("Bearer s3cret", call[1]);
        var dispatch = Assert.Single(Lines(audit));
        Assert.Equal("dispatch", dispatch.GetProperty("kind").GetString());
        Assert.Equal(call[2], dispatch.GetProperty("job").GetString());
    }

    [Fact]
    public async Task RequireSignedExecutor_Refuses_A_Clear_Text_Executor_Without_Contacting_It()
    {
        var (app, http, audit) = await Host(("Bowire:Parallel:RequireSignedExecutor", "true"));
        await using var _a = app;

        var resp = await http.PostAsJsonAsync("/api/parallel/start", new
        {
            targets = new[] { new { url = "https://api.example/x", method = "GET" } },
            sessionCount = 2,
            hosts = TestNetExecutor,
        }, Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        var host = Assert.Single(doc.RootElement.GetProperty("hosts").EnumerateArray());
        Assert.StartsWith("refused:", host.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.True(Lines(audit)[0].GetProperty("requireSignedExecutor").GetBoolean());
    }

    [Fact]
    public async Task A_Coordinator_Running_Locally_Honours_Its_Own_Allowlist()
    {
        var (up, upUrl, counter, _) = await Upstream();
        await using var _u = up;
        var (app, http, _) = await Host(("Bowire:Parallel:TargetAllowlist", "https://only.this.example/*"));
        await using var _a = app;

        var resp = await http.PostAsJsonAsync("/api/parallel/start", Job(upUrl + "/x"), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(0, counter.Hits);
    }
}
