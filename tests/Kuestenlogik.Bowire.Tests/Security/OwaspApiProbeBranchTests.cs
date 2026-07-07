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
/// Branch coverage for the OWASP HTTP probes beyond their headline finding —
/// the CORS / security-header variants (API8), the inventory sub-checks
/// (API9), and the BOLA guards (API1) — against shaped loopback upstreams.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class OwaspApiProbeBranchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly List<string> s_noAuth = [];
    private static readonly List<string> s_authA = ["Authorization: Bearer alice"];
    private static readonly List<string> s_authB = ["Authorization: Bearer bob"];

    // ---------- API8 variants ----------

    [Fact]
    public async Task Api8_WildcardCors_FlagsPermissive()
    {
        await using var up = await StartAsync(ctx =>
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }, Ct);
        using var http = NewClient();

        var findings = await new Api8ConfigProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);
        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API8-CORS-PERMISSIVE");
    }

    [Fact]
    public async Task Api8_MissingContentTypeOptions_FlagsXcto()
    {
        await using var up = await StartAsync(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; }, Ct);
        using var http = NewClient();

        var findings = await new Api8ConfigProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);
        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API8-XCTO");
    }

    [Fact]
    public async Task Api8_HtmlWithoutCspOrFrameOptions_FlagsClickjackingAndCsp()
    {
        await using var up = await StartAsync(async ctx =>
        {
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.ContentType = "text/html";
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("<html></html>", ctx.RequestAborted);
        }, Ct);
        using var http = NewClient();

        var findings = await new Api8ConfigProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);
        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API8-CSP");
        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API8-XFO");
    }

    [Fact]
    public async Task Api8_UnreachableTarget_Skips()
    {
        using var http = NewClient();
        var f = Assert.Single(await new Api8ConfigProbe().RunAsync("http://127.0.0.1:9/", http, s_noAuth, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
    }

    // ---------- API9 variants ----------

    [Fact]
    public async Task Api9_OlderVersionRouted_FlagsVulnerable()
    {
        await using var up = await StartAsync(ctx =>
        {
            // /v1/... still answers (older version live); the random baseline 404s.
            ctx.Response.StatusCode = ctx.Request.Path.Value!.Contains("/v1/", StringComparison.Ordinal) ? 200 : 404;
            return Task.CompletedTask;
        }, Ct);
        using var http = NewClient();
        var target = up.Urls.First().TrimEnd('/') + "/api/v2/users";

        var findings = await new Api9InventoryProbe().RunAsync(target, http, s_noAuth, s_noAuth, Ct);
        Assert.Contains(findings, f => f.Status == ScanFindingStatus.Vulnerable
            && (f.Template.Recording.Vulnerability?.Id?.Contains("API9-VER", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task Api9_DeprecationHeaderStillServed_FlagsVulnerable()
    {
        await using var up = await StartAsync(ctx =>
        {
            ctx.Response.Headers["Sunset"] = "Wed, 01 Jan 2025 00:00:00 GMT";
            ctx.Response.StatusCode = ctx.Request.Path == "/" ? 200 : 404;
            return Task.CompletedTask;
        }, Ct);
        using var http = NewClient();

        var findings = await new Api9InventoryProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);
        Assert.Contains(findings, f => f.Status == ScanFindingStatus.Vulnerable
            && (f.Template.Recording.Vulnerability?.Id?.Contains("DEPRECATED", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task Api9_CatchAllRoute_Suppressed()
    {
        await using var up = await StartAsync(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; }, Ct);
        using var http = NewClient();

        var findings = await new Api9InventoryProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);
        Assert.Contains(findings, f => f.Status == ScanFindingStatus.Skipped
            && f.Template.Recording.Id.Contains("API9-CATCHALL", StringComparison.Ordinal));
    }

    // ---------- API1 guards ----------

    [Fact]
    public async Task Api1_SameIdentity_Skips()
    {
        using var http = NewClient();
        var f = Assert.Single(await new Api1BolaProbe().RunAsync(
            "https://api.example.com/orders/123", http, s_authA, s_authA, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API1-SAME-IDENTITY", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api1_AnonymousAccessNotBlocked_DoesNotFlagBola()
    {
        // Everyone (incl. anon) gets 200 → it's a public resource, not BOLA.
        await using var up = await StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"id\":123}", ctx.RequestAborted);
        }, Ct);
        using var http = NewClient();
        var target = up.Urls.First().TrimEnd('/') + "/orders/123";

        var findings = await new Api1BolaProbe().RunAsync(target, http, s_authA, s_authB, Ct);
        Assert.DoesNotContain(findings, f => f.Status == ScanFindingStatus.Vulnerable);
    }

    [Fact]
    public async Task Api1_InvalidTarget_Errors()
    {
        using var http = NewClient();
        var f = Assert.Single(await new Api1BolaProbe().RunAsync("not a url", http, s_authA, s_authB, Ct));
        Assert.Equal(ScanFindingStatus.Error, f.Status);
    }

    [Fact]
    public async Task Api1_NoObjectIdInPath_Skips()
    {
        using var http = NewClient();
        var f = Assert.Single(await new Api1BolaProbe().RunAsync("https://api.example.com/orders", http, s_authA, s_authB, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API1-NO-OBJECT-ID", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    // ---------- API3 guards ----------

    [Fact]
    public async Task Api3_WriteDenied_Skips()
    {
        await using var up = await StartAsync(async ctx =>
        {
            if (ctx.Request.Method == "PATCH") { ctx.Response.StatusCode = 403; return; }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"id\":1}", ctx.RequestAborted);
        }, Ct);
        using var http = NewClient();

        var f = Assert.Single(await new Api3BoplaProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API3-WRITE-DENIED", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api3_PatchUnsupported_Skips()
    {
        await using var up = await StartAsync(async ctx =>
        {
            if (ctx.Request.Method == "PATCH") { ctx.Response.StatusCode = 405; return; }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"id\":1}", ctx.RequestAborted);
        }, Ct);
        using var http = NewClient();

        var f = Assert.Single(await new Api3BoplaProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API3-NO-PATCH", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api3_UnknownPropertyRejected_ReportsSafe()
    {
        await using var up = await StartAsync(async ctx =>
        {
            if (ctx.Request.Method == "PATCH") { ctx.Response.StatusCode = 400; return; } // canary rejected
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"id\":1}", ctx.RequestAborted);
        }, Ct);
        using var http = NewClient();

        var findings = await new Api3BoplaProbe().RunAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);
        Assert.Contains(findings, f => f.Status == ScanFindingStatus.Safe);
        Assert.DoesNotContain(findings, f => f.Status == ScanFindingStatus.Vulnerable);
    }

    // ---------- helpers ----------

    private static HttpClient NewClient()
        => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };

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
