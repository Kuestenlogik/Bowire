// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Security.Scanner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the shipped OWASP HTTP probes (API1–API5, API7–API9) driven
/// against a loopback Kestrel upstream. Complements the API6/API10 tests in
/// <see cref="OwaspHttpProbeTests"/>; together they exercise the per-entry
/// probe verdict logic without the live vulnerable-by-design sample app.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class OwaspApiProbeCoverageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly List<string> s_noAuth = [];
    private static readonly List<string> s_authA = ["Authorization: Bearer identity-a"];
    private static readonly List<string> s_authB = ["Authorization: Bearer identity-b"];

    // ---------- API4 — resource consumption ----------

    [Fact]
    public async Task Api4_NoThrottleAndLargeBodyAccepted_FlagsBoth()
    {
        await using var up = await StartAsync(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; }, Ct);
        using var http = NewClient();

        var findings = await new Api4ResourceProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);

        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API4-RATELIMIT");
        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API4-BODYSIZE");
    }

    [Fact]
    public async Task Api4_ThrottledAndBodyCapped_ReportsSafe()
    {
        await using var up = await StartAsync(ctx =>
        {
            ctx.Response.StatusCode = ctx.Request.Method == "POST" ? 413 : 429;
            ctx.Response.Headers["Retry-After"] = "1";
            return Task.CompletedTask;
        }, Ct);
        using var http = NewClient();

        var findings = await new Api4ResourceProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);
        Assert.Contains(findings, f => f.Status == ScanFindingStatus.Safe);
        Assert.DoesNotContain(findings, f => f.Status == ScanFindingStatus.Vulnerable);
    }

    // ---------- API5 — broken function level authorization ----------

    [Fact]
    public async Task Api5_PublicPrivilegedEndpoint_FlagsVulnerable()
    {
        await using var up = await StartAsync(ctx =>
        {
            ctx.Response.StatusCode = ctx.Request.Path == "/actuator/env" ? 200 : 404;
            return Task.CompletedTask;
        }, Ct);
        using var http = NewClient();

        var findings = await new Api5AuthorizationProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);
        Assert.Contains(findings, f => f.Status == ScanFindingStatus.Vulnerable
            && (f.Template.Recording.Vulnerability?.OwaspApi?.StartsWith("API5", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task Api5_AllPrivilegedGated_ReportsSafe()
    {
        await using var up = await StartAsync(ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; }, Ct);
        using var http = NewClient();

        var f = Assert.Single(await new Api5AuthorizationProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
    }

    [Fact]
    public async Task Api5_CatchAllRoute_Suppressed()
    {
        await using var up = await StartAsync(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; }, Ct);
        using var http = NewClient();

        var f = Assert.Single(await new Api5AuthorizationProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
    }

    // ---------- API8 — security misconfiguration ----------

    [Fact]
    public async Task Api8_ReflectedOriginWithCredentials_FlagsHigh()
    {
        await using var up = await StartAsync(ctx =>
        {
            var origin = ctx.Request.Headers["Origin"].ToString();
            if (!string.IsNullOrEmpty(origin))
            {
                ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
                ctx.Response.Headers["Access-Control-Allow-Credentials"] = "true";
            }
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }, Ct);
        using var http = NewClient();

        var findings = await new Api8ConfigProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);
        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API8-CORS-REFLECT-CREDS");
    }

    [Fact]
    public async Task Api8_HardenedHeaders_NoCorsFinding()
    {
        await using var up = await StartAsync(ctx =>
        {
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }, Ct);
        using var http = NewClient();

        var findings = await new Api8ConfigProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);
        Assert.DoesNotContain(findings, f => (f.Template.Recording.Vulnerability?.Id?.Contains("CORS", StringComparison.Ordinal) ?? false));
    }

    // ---------- API9 — improper inventory management ----------

    [Fact]
    public async Task Api9_PublicDocSurface_FlagsVulnerable()
    {
        await using var up = await StartAsync(ctx =>
        {
            ctx.Response.StatusCode = ctx.Request.Path == "/openapi.json" ? 200 : 404;
            return Task.CompletedTask;
        }, Ct);
        using var http = NewClient();

        var findings = await new Api9InventoryProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);
        Assert.Contains(findings, f => f.Status == ScanFindingStatus.Vulnerable
            && (f.Template.Recording.Vulnerability?.Id?.Contains("API9-DOC", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task Api9_NothingExposed_ReportsSafe()
    {
        await using var up = await StartAsync(ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; }, Ct);
        using var http = NewClient();

        var f = Assert.Single(await new Api9InventoryProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
    }

    // ---------- API1 — BOLA ----------

    [Fact]
    public async Task Api1_MissingSecondIdentity_Skips()
    {
        using var http = NewClient();
        var f = Assert.Single(await new Api1BolaProbe().RunAsync("https://api.example.com/orders/123", http, s_authA, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API1-NEEDS-TWO-IDENTITIES", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api1_BobReadsAlicesObjectWhileAnonBlocked_FlagsVulnerable()
    {
        await using var up = await StartAsync(ctx =>
        {
            var authed = !string.IsNullOrEmpty(ctx.Request.Headers.Authorization.ToString());
            ctx.Response.StatusCode = authed ? 200 : 401;
            return authed ? ctx.Response.WriteAsync("{\"id\":123,\"owner\":\"alice\"}", ctx.RequestAborted) : Task.CompletedTask;
        }, Ct);
        using var http = NewClient();

        var target = up.Urls.First().TrimEnd('/') + "/orders/123";
        var findings = await new Api1BolaProbe().RunAsync(target, http, s_authA, s_authB, Ct);
        Assert.Contains(findings, f => f.Status == ScanFindingStatus.Vulnerable
            && (f.Template.Recording.Vulnerability?.OwaspApi?.StartsWith("API1", StringComparison.Ordinal) ?? false));
    }

    // ---------- skip-path guards (API2 / API3 / API7) ----------

    [Fact]
    public async Task Api2_NoCredential_Skips()
    {
        await using var up = await StartAsync(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; }, Ct);
        using var http = NewClient();
        var findings = await new Api2AuthProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);
        Assert.All(findings, f => Assert.NotEqual(ScanFindingStatus.Vulnerable, f.Status));
    }

    [Fact]
    public async Task Api3_NonObjectTarget_Skips()
    {
        await using var up = await StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("not json", ctx.RequestAborted);
        }, Ct);
        using var http = NewClient();
        var f = Assert.Single(await new Api3BoplaProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
    }

    [Fact]
    public async Task Api7_NoUrlParameter_Skips()
    {
        using var http = NewClient();
        var f = Assert.Single(await new Api7SsrfProbe().RunAsync("https://api.example.com/health", http, s_noAuth, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
    }

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
