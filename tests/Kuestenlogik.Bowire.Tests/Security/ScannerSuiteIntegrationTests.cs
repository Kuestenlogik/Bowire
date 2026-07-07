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
/// Integration coverage for the OWASP suite runner (<see cref="OwaspApiSuite.RunProbesAsync"/>
/// driving all ten HTTP probes) plus the API7 SSRF timing probe's live path,
/// against a loopback Kestrel upstream.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class ScannerSuiteIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly List<string> s_noAuth = [];

    [Fact]
    public async Task RunProbesAsync_DrivesAllTenProbes_ProducesTaggedFindings()
    {
        await using var up = await StartAsync(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; }, Ct);
        using var http = NewClient();

        var findings = await OwaspApiSuite.RunProbesAsync(up.Urls.First(), http, s_noAuth, s_noAuth, Ct);

        Assert.NotEmpty(findings);
        // Every finding the suite emits carries an OWASP tag; several distinct
        // entries should be represented after a full ten-probe pass.
        var entries = findings
            .Select(f => OwaspApiCatalog.Match(f.Template.Recording.Vulnerability?.OwaspApi)?.Id)
            .Where(id => id is not null)
            .Distinct()
            .ToList();
        Assert.True(entries.Count >= 5, $"expected findings across ≥5 OWASP entries, got {entries.Count}");
    }

    [Fact]
    public async Task Api7_UrlParamNoServerFetch_ReportsSafe()
    {
        // Upstream ignores the url param and answers instantly, so swapping it
        // for the blackhole address produces no latency stall → not vulnerable.
        await using var up = await StartAsync(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; }, Ct);
        using var http = NewClient();
        var target = up.Urls.First().TrimEnd('/') + "/fetch?url=http://example.com/image.png";

        var findings = await new Api7SsrfProbe().RunAsync(target, http, s_noAuth, s_noAuth, Ct);

        Assert.DoesNotContain(findings, f => f.Status == ScanFindingStatus.Vulnerable);
        Assert.NotEmpty(findings);
    }

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
