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
/// #187 CVE lookup: version-range matching, banner parsing, VulnDb JSON
/// loading, and the passive <see cref="ServerCveProbe"/> against a loopback
/// upstream that advertises a vulnerable banner.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class CveLookupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("1.18.0", "1.21.0", -1)]
    [InlineData("1.21.0", "1.21.0", 0)]
    [InlineData("2.4.51", "2.4.49", 1)]
    [InlineData("2.4.9", "2.4.49", -1)] // component-wise numeric, not lexical
    [InlineData("1.0", "1.0.0", 0)]     // missing components = 0
    public void CompareVersions_IsComponentWiseNumeric(string a, string b, int expected)
        => Assert.Equal(expected, Math.Sign(CveDatabase.CompareVersions(a, b)));

    [Fact]
    public void Match_RespectsProductAndRange()
    {
        var db = CveDatabase.Seed();
        // nginx 1.18.0 is inside [0.6.18, 1.21.0) → CVE-2021-23017.
        Assert.Contains(db.Match("nginx", "1.18.0"), e => e.Cve == "CVE-2021-23017");
        // Fixed version → no match.
        Assert.Empty(db.Match("nginx", "1.21.0"));
        // Apache 2.4.49 → both path-traversal CVEs.
        Assert.Equal(2, db.Match("Apache", "2.4.49").Count);
        // Apache 2.4.51 → patched.
        Assert.Empty(db.Match("Apache", "2.4.51"));
        // Product mismatch.
        Assert.Empty(db.Match("lighttpd", "1.4.0"));
    }

    [Theory]
    [InlineData("nginx/1.18.0", "nginx", "1.18.0")]
    [InlineData("Apache/2.4.49 (Ubuntu)", "Apache", "2.4.49")]
    [InlineData("Microsoft-IIS/10.0", "Microsoft-IIS", "10.0")]
    [InlineData("PHP/7.4.3", "PHP", "7.4.3")]
    public void ParseBanner_ExtractsProductAndVersion(string banner, string product, string version)
    {
        var (p, v) = ServerCveProbe.ParseBanner(banner);
        Assert.Equal(product, p);
        Assert.Equal(version, v);
    }

    [Theory]
    [InlineData("Express")]      // no version
    [InlineData("nginx")]        // no slash
    [InlineData("nginx/")]       // empty version
    [InlineData("cloudflare")]   // no version
    public void ParseBanner_NoVersion_ReturnsNulls(string banner)
    {
        var (p, v) = ServerCveProbe.ParseBanner(banner);
        Assert.Null(p);
        Assert.Null(v);
    }

    [Fact]
    public void Parse_AcceptsArrayAndEntriesEnvelope()
    {
        var arr = CveDatabase.Parse("""[ { "product":"foo", "cve":"CVE-1", "introduced":"1.0", "fixed":"2.0" } ]""");
        Assert.Equal(1, arr.Count);
        var env = CveDatabase.Parse("""{ "entries": [ { "product":"foo", "cve":"CVE-1" }, { "product":"bar", "cve":"CVE-2" } ] }""");
        Assert.Equal(2, env.Count);
    }

    [Fact]
    public async Task Probe_VulnerableBanner_FlagsCve()
    {
        await using var up = await StartAsync("TestProd/1.0.0", Ct);
        using var http = new HttpClient();
        var db = new CveDatabase(
        [
            new CveEntry("TestProd", "CVE-TEST-1", Introduced: "1.0.0", Fixed: "2.0.0", Severity: "high", Cvss: 7.5,
                Title: "test vuln", Reference: "https://example.com/cve"),
        ]);

        var findings = await ServerCveProbe.RunAsync(up.Urls.First(), http, [], db, Ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-CVE-CVE-TEST-1", f.Template.Recording.Vulnerability?.Id);
        Assert.Contains("TestProd 1.0.0", f.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_PatchedBanner_ReportsClean()
    {
        await using var up = await StartAsync("TestProd/2.0.0", Ct);
        using var http = new HttpClient();
        var db = new CveDatabase(
        [
            new CveEntry("TestProd", "CVE-TEST-1", Introduced: "1.0.0", Fixed: "2.0.0"),
        ]);

        var findings = await ServerCveProbe.RunAsync(up.Urls.First(), http, [], db, Ct);
        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("CVE-CLEAN", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    // Loopback upstream that advertises the given banner via X-Powered-By
    // (Kestrel manages the Server header, so the test drives the probe through
    // the X-Powered-By path — the probe reads both).
    private static async Task<WebApplication> StartAsync(string banner, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.Run(ctx =>
        {
            ctx.Response.Headers["X-Powered-By"] = banner;
            return ctx.Response.WriteAsync("ok", ctx.RequestAborted);
        });
        await app.StartAsync(ct);
        return app;
    }
}
