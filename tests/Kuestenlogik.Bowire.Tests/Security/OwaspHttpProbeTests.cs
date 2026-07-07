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
/// Coverage for the two OWASP HTTP probes added in the v2.3 suite completion —
/// <see cref="Api6BusinessFlowProbe"/> (business-flow friction) and
/// <see cref="Api10UnsafeConsumptionProbe"/> (unsafe upstream consumption) —
/// exercised against a loopback Kestrel upstream returning shaped responses.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class OwaspHttpProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly List<string> s_noAuth = [];

    // ---------- API6 ----------

    [Fact]
    public async Task Api6_RepeatedPostsNoFriction_FlagsVulnerable()
    {
        await using var upstream = await StartAsync(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; }, Ct);
        using var http = NewClient();

        var findings = await new Api6BusinessFlowProbe().RunAsync(upstream.Urls.First(), http, s_noAuth, s_noAuth, Ct);

        var f = Assert.Single(findings, x => x.Status == ScanFindingStatus.Vulnerable);
        Assert.Equal("BWR-OWASP-API6-NO-FRICTION", f.Template.Recording.Vulnerability?.Id);
        Assert.Equal("API6-2023-BUSFLOW", f.Template.Recording.Vulnerability?.OwaspApi);
    }

    [Fact]
    public async Task Api6_ThrottleHeaderPresent_ReportsSafe()
    {
        await using var upstream = await StartAsync(ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers["Retry-After"] = "5";
            return Task.CompletedTask;
        }, Ct);
        using var http = NewClient();

        var findings = await new Api6BusinessFlowProbe().RunAsync(upstream.Urls.First(), http, s_noAuth, s_noAuth, Ct);

        Assert.Single(findings, x => x.Status == ScanFindingStatus.Safe);
        Assert.DoesNotContain(findings, x => x.Status == ScanFindingStatus.Vulnerable);
    }

    [Fact]
    public async Task Api6_PostRejected_SkipsNoFlow()
    {
        await using var upstream = await StartAsync(ctx =>
        {
            ctx.Response.StatusCode = ctx.Request.Method == "POST" ? 405 : 200;
            return Task.CompletedTask;
        }, Ct);
        using var http = NewClient();

        var f = Assert.Single(await new Api6BusinessFlowProbe().RunAsync(upstream.Urls.First(), http, s_noAuth, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API6-NO-FLOW", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    // ---------- API10 ----------

    [Fact]
    public async Task Api10_ReflectedUpstreamError_FlagsVulnerable()
    {
        await using var upstream = await StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsync("upstream connect error or disconnect/reset before headers", ctx.RequestAborted);
        }, Ct);
        using var http = NewClient();

        var findings = await new Api10UnsafeConsumptionProbe().RunAsync(upstream.Urls.First(), http, s_noAuth, s_noAuth, Ct);

        var f = Assert.Single(findings, x => x.Status == ScanFindingStatus.Vulnerable);
        Assert.Equal("BWR-OWASP-API10-UPSTREAM-ERROR", f.Template.Recording.Vulnerability?.Id);
    }

    [Fact]
    public async Task Api10_OffHostRedirect_FlagsVulnerable()
    {
        await using var upstream = await StartAsync(ctx =>
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers["Location"] = "https://third-party.example.com/callback";
            return Task.CompletedTask;
        }, Ct);
        using var http = NewClient();

        var findings = await new Api10UnsafeConsumptionProbe().RunAsync(upstream.Urls.First(), http, s_noAuth, s_noAuth, Ct);

        Assert.Contains(findings, x => x.Status == ScanFindingStatus.Vulnerable
            && x.Template.Recording.Vulnerability?.Id == "BWR-OWASP-API10-OFFHOST-REDIRECT");
    }

    [Fact]
    public async Task Api10_CleanResponse_SkipsReviewOnly()
    {
        await using var upstream = await StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("{\"ok\":true}", ctx.RequestAborted);
        }, Ct);
        using var http = NewClient();

        var f = Assert.Single(await new Api10UnsafeConsumptionProbe().RunAsync(upstream.Urls.First(), http, s_noAuth, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API10-REVIEW-ONLY", f.Template.Recording.Id, StringComparison.Ordinal);
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
