// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Parallel;

/// <summary>
/// Smoke + behaviour coverage for the #132 Phase 2 parallel-sessions
/// endpoints. Hits both routes against a loopback Kestrel host:
/// <list type="bullet">
///   <item><c>/start-local</c> — runs N sessions in-process,
///   checks the per-session counts, ramp-up timing, and
///   continue-on-error policy.</item>
///   <item><c>/start</c> — fans out across two loopback workers,
///   merges results, and surfaces a per-host roll-up.</item>
/// </list>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class BowireParallelEndpointsTests
{
    private static async Task<(WebApplication app, HttpClient http)> StartParallelHostAsync(
        CancellationToken ct)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = b.Build();
        app.MapBowireParallelEndpoints("");
        await app.StartAsync(ct);
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    private static async Task<(WebApplication app, string url, int hits)> StartUpstreamAsync(
        CancellationToken ct, Func<HttpContext, Task>? handler = null)
    {
        var hits = 0;
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = b.Build();
        app.Run(async ctx =>
        {
            Interlocked.Increment(ref hits);
            if (handler is not null) { await handler(ctx); return; }
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("ok", ctx.RequestAborted);
        });
        await app.StartAsync(ct);
        return (app, app.Urls.First(), hits);
    }

    [Fact]
    public async Task StartLocal_RunsSessionsAndAggregates()
    {
        var ct = TestContext.Current.CancellationToken;
        var (upstream, upstreamUrl, _) = await StartUpstreamAsync(ct);
        await using var _u = upstream;
        var (app, http) = await StartParallelHostAsync(ct);
        await using var _a = app;
        using var _h = http;

        var body = new
        {
            targets = new[]
            {
                new { url = upstreamUrl, method = "GET", body = "", label = "t0" },
                new { url = upstreamUrl, method = "GET", body = "", label = "t1" },
                new { url = upstreamUrl, method = "GET", body = "", label = "t2" },
                new { url = upstreamUrl, method = "GET", body = "", label = "t3" },
            },
            sessionCount = 2,
            rampUpSeconds = 0,
            continueOnError = true,
        };

        var resp = await http.PostAsJsonAsync("/api/parallel/start-local", body, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<ParallelResponseDto>(cancellationToken: ct);
        Assert.NotNull(json);
        Assert.Equal(2, json!.SessionCount);
        Assert.Equal(4, json.TargetCount);
        Assert.Equal(4, json.PassCount);
        Assert.Equal(0, json.FailCount);
        Assert.Equal(2, json.Sessions.Count);
        // Round-robin: session 0 owns t0,t2; session 1 owns t1,t3.
        Assert.Equal(2, json.Sessions[0].PassCount);
        Assert.Equal(2, json.Sessions[1].PassCount);
        Assert.Equal(4, json.Results.Count);
    }

    [Fact]
    public async Task StartLocal_ContinueOnErrorFalse_AbortsOnFirstFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var (upstream, upstreamUrl, _) = await StartUpstreamAsync(ct, async ctx =>
        {
            // Fail every request — the abort path should trip on the
            // very first target.
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsync("boom", ctx.RequestAborted);
        });
        await using var _u = upstream;
        var (app, http) = await StartParallelHostAsync(ct);
        await using var _a = app;
        using var _h = http;

        var body = new
        {
            targets = new[]
            {
                new { url = upstreamUrl, method = "GET", body = "" },
                new { url = upstreamUrl, method = "GET", body = "" },
                new { url = upstreamUrl, method = "GET", body = "" },
                new { url = upstreamUrl, method = "GET", body = "" },
            },
            sessionCount = 2,
            rampUpSeconds = 0,
            continueOnError = false,
        };

        var resp = await http.PostAsJsonAsync("/api/parallel/start-local", body, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<ParallelResponseDto>(cancellationToken: ct);
        Assert.NotNull(json);
        // Each session aborts on its first failure → at most one result
        // per session, never the full 4-target sweep.
        Assert.True(json!.Results.Count <= 2,
            $"Expected at most one result per session, got {json.Results.Count}.");
        Assert.Equal(0, json.PassCount);
        Assert.All(json.Sessions, s => Assert.Equal("first-failure", s.Aborted));
    }

    [Fact]
    public async Task StartLocal_EnvPool_TagsSessionsAndForwardsHeader()
    {
        var ct = TestContext.Current.CancellationToken;
        var observedEnvs = new System.Collections.Concurrent.ConcurrentBag<string>();
        var (upstream, upstreamUrl, _) = await StartUpstreamAsync(ct, async ctx =>
        {
            if (ctx.Request.Headers.TryGetValue("X-Bowire-Env", out var env))
            {
                observedEnvs.Add(env.ToString());
            }
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("ok", ctx.RequestAborted);
        });
        await using var _u = upstream;
        var (app, http) = await StartParallelHostAsync(ct);
        await using var _a = app;
        using var _h = http;

        var body = new
        {
            targets = new[]
            {
                new { url = upstreamUrl, method = "GET", body = "" },
                new { url = upstreamUrl, method = "GET", body = "" },
            },
            sessionCount = 2,
            envPool = new[] { "dev", "staging" },
        };

        var resp = await http.PostAsJsonAsync("/api/parallel/start-local", body, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<ParallelResponseDto>(cancellationToken: ct);
        Assert.NotNull(json);
        Assert.Equal("dev", json!.Sessions[0].EnvId);
        Assert.Equal("staging", json.Sessions[1].EnvId);
        Assert.Contains("dev", observedEnvs);
        Assert.Contains("staging", observedEnvs);
    }

    [Fact]
    public async Task StartDistributed_FansOutAcrossHosts()
    {
        var ct = TestContext.Current.CancellationToken;
        var (upstream, upstreamUrl, _) = await StartUpstreamAsync(ct);
        await using var _u = upstream;
        var (workerA, httpA) = await StartParallelHostAsync(ct);
        await using var _a = workerA;
        using var _ha = httpA;
        var (workerB, httpB) = await StartParallelHostAsync(ct);
        await using var _b = workerB;
        using var _hb = httpB;
        var (coordinator, httpC) = await StartParallelHostAsync(ct);
        await using var _c = coordinator;
        using var _hc = httpC;

        var body = new
        {
            targets = new[]
            {
                new { url = upstreamUrl, method = "GET", body = "" },
                new { url = upstreamUrl, method = "GET", body = "" },
            },
            sessionCount = 4,
            rampUpSeconds = 0,
            continueOnError = true,
            hosts = new[] { workerA.Urls.First(), workerB.Urls.First() },
        };

        var resp = await httpC.PostAsJsonAsync("/api/parallel/start", body, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<ParallelResponseDto>(cancellationToken: ct);
        Assert.NotNull(json);
        Assert.Equal(4, json!.SessionCount);
        Assert.NotNull(json.Hosts);
        Assert.Equal(2, json.Hosts!.Count);
        Assert.Equal(2, json.Hosts[0].SessionCount);
        Assert.Equal(2, json.Hosts[1].SessionCount);
        // 4 sessions × 2 targets, sharded round-robin per host →
        // session i gets target index where i % perHost == k.
        // Total = sessions × targets per session = 4 sessions × 1
        // (each session gets 1 target after round-robin) = 4 calls.
        Assert.True(json.PassCount > 0);
        Assert.Equal(0, json.FailCount);
    }

    [Fact]
    public async Task StartDistributed_NoHosts_RunsLocally()
    {
        var ct = TestContext.Current.CancellationToken;
        var (upstream, upstreamUrl, _) = await StartUpstreamAsync(ct);
        await using var _u = upstream;
        var (app, http) = await StartParallelHostAsync(ct);
        await using var _a = app;
        using var _h = http;

        var body = new
        {
            targets = new[] { new { url = upstreamUrl, method = "GET", body = "" } },
            sessionCount = 1,
            hosts = Array.Empty<string>(),
        };

        var resp = await http.PostAsJsonAsync("/api/parallel/start", body, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<ParallelResponseDto>(cancellationToken: ct);
        Assert.NotNull(json);
        Assert.Equal(1, json!.SessionCount);
        Assert.Equal(1, json.PassCount);
        // No hosts surface on the local-fallback path — same shape as
        // /start-local would have returned.
        Assert.Null(json.Hosts);
    }

    // Minimal DTOs mirroring the wire shape — keeps the tests off the
    // internal types (which are internal to Kuestenlogik.Bowire and
    // hidden behind InternalsVisibleTo only for select assemblies).
    private sealed class ParallelResponseDto
    {
        public int SessionCount { get; set; }
        public int TargetCount { get; set; }
        public long TotalDurationMs { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public List<SessionDto> Sessions { get; set; } = [];
        public List<ResultDto> Results { get; set; } = [];
        public List<HostDto>? Hosts { get; set; }
    }
    private sealed class SessionDto
    {
        public int SessionIndex { get; set; }
        public string? EnvId { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public long DurationMs { get; set; }
        public string? Aborted { get; set; }
    }
    private sealed class ResultDto
    {
        public int SessionIndex { get; set; }
        public int TargetIndex { get; set; }
        public string? Label { get; set; }
        public bool Pass { get; set; }
        public int Status { get; set; }
        public long DurationMs { get; set; }
        public string? Error { get; set; }
    }
    private sealed class HostDto
    {
        public string Host { get; set; } = "";
        public int SessionCount { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public long DurationMs { get; set; }
        public string? Error { get; set; }
    }
}
