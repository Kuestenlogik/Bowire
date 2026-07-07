// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using Kuestenlogik.Bowire.Security.Scanner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the two active-request OWASP probes — <see cref="Api2AuthProbe"/>
/// (broken authentication: unauth access, JWT alg:none / tamper / expiry) and
/// <see cref="Api3BoplaProbe"/> (mass assignment) — against a loopback upstream
/// whose auth behaviour is shaped per test.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class Api2Api3ProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly List<string> s_noAuth = [];

    private static string B64Url(string s)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Jwt(string payload)
        => $"{B64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")}.{B64Url(payload)}.{B64Url("sig")}";

    // ---------- API2 ----------

    [Fact]
    public async Task Api2_AcceptsForgedAndUnexpiringToken_FlagsCritical()
    {
        // Upstream accepts ANY Authorization header (never verifies the token).
        await using var up = await StartAsync(ctx =>
        {
            ctx.Response.StatusCode = string.IsNullOrEmpty(ctx.Request.Headers.Authorization.ToString()) ? 401 : 200;
            return Task.CompletedTask;
        }, Ct);
        using var http = NewClient();
        var auth = new List<string> { "Authorization: Bearer " + Jwt("{\"sub\":\"1\"}") }; // no exp

        var findings = await new Api2AuthProbe().RunAsync(up.Urls.First(), http, auth, s_noAuth, Ct);

        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API2-ALG-NONE");
        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API2-NOEXP");
    }

    [Fact]
    public async Task Api2_ProperlyValidatingServer_ReportsSafe()
    {
        var token = Jwt("{\"sub\":\"1\",\"exp\":9999999999}");
        var exact = "Bearer " + token;
        await using var up = await StartAsync(ctx =>
        {
            ctx.Response.StatusCode = ctx.Request.Headers.Authorization.ToString() == exact ? 200 : 401;
            return Task.CompletedTask;
        }, Ct);
        using var http = NewClient();

        var findings = await new Api2AuthProbe().RunAsync(up.Urls.First(), http, ["Authorization: " + exact], s_noAuth, Ct);

        Assert.DoesNotContain(findings, f => f.Status == ScanFindingStatus.Vulnerable);
        Assert.Contains(findings, f => f.Status == ScanFindingStatus.Safe);
    }

    [Fact]
    public async Task Api2_ExpiredTokenAccepted_FlagsHigh()
    {
        await using var up = await StartAsync(ctx =>
        {
            ctx.Response.StatusCode = string.IsNullOrEmpty(ctx.Request.Headers.Authorization.ToString()) ? 401 : 200;
            return Task.CompletedTask;
        }, Ct);
        using var http = NewClient();
        var auth = new List<string> { "Authorization: Bearer " + Jwt("{\"sub\":\"1\",\"exp\":1}") }; // long expired

        var findings = await new Api2AuthProbe().RunAsync(up.Urls.First(), http, auth, s_noAuth, Ct);
        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API2-EXPIRED");
    }

    [Fact]
    public async Task Api2_NoBaseline_Skips()
    {
        await using var up = await StartAsync(ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; }, Ct);
        using var http = NewClient();
        var f = Assert.Single(await new Api2AuthProbe().RunAsync(up.Urls.First(), http, ["Authorization: Bearer x"], s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
    }

    // ---------- API3 ----------

    [Fact]
    public async Task Api3_PersistsCanaryProperty_FlagsVulnerable()
    {
        var state = new StateBox { Body = "{\"id\":1,\"name\":\"widget\"}" };
        await using var up = await StartAsync(async ctx =>
        {
            if (ctx.Request.Method == "PATCH")
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync(ctx.RequestAborted);
                if (body.Contains("bowireProbe", StringComparison.Ordinal)) state.Body = body; // persist the canary
                ctx.Response.StatusCode = 200;
                return;
            }
            ctx.Response.ContentType = "application/json";
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync(state.Body, ctx.RequestAborted);
        }, Ct);
        using var http = NewClient();

        var findings = await new Api3BoplaProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);
        Assert.Contains(findings, f => f.Status == ScanFindingStatus.Vulnerable
            && (f.Template.Recording.Vulnerability?.OwaspApi?.StartsWith("API3", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task Api3_CanaryNotPersisted_ReportsSafe()
    {
        await using var up = await StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"id\":1,\"name\":\"widget\"}", ctx.RequestAborted); // PATCH ignored; canary never echoed
        }, Ct);
        using var http = NewClient();

        var findings = await new Api3BoplaProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);
        Assert.DoesNotContain(findings, f => f.Status == ScanFindingStatus.Vulnerable);
    }

    private sealed class StateBox { public string Body { get; set; } = ""; }

    // ---------- helpers ----------

    private static HttpClient NewClient()
        => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    private static async Task<WebApplication> StartAsync(RequestDelegate handler, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.Run(handler);
        await app.StartAsync(ct);
        return app;
    }
}
