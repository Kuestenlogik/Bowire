// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.Security.Scanner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for <see cref="SecurityBuiltins"/> — the built-in passive
/// checks run alongside JSON-template scans. Exercises:
/// <list type="bullet">
///   <item>The plaintext-http guard (http:// target → CWE-319 finding).</item>
///   <item>The malformed-URL guard (unparseable target → Error finding).</item>
///   <item>Banner-disclosure detection (X-Powered-By + Server headers).</item>
///   <item>Verbose-error detection (.NET stack-trace shape in body).</item>
/// </list>
/// The TLS-version probes that hit a https:// target need a Kestrel
/// HTTPS listener which is awkward to stand up cross-platform; those
/// land via the live <see cref="ScanCommandTests"/> integration runs
/// against the vulnerable-by-design sample app (out of scope here).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861:Prefer static readonly fields over constant array arguments", Justification = "Test scope")]
public sealed class SecurityBuiltinsTests
{
    [Fact]
    public async Task RunAllAsync_PlaintextHttpTarget_EmitsCwe319Finding()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(_ => Task.CompletedTask, ct);

        using var http = new HttpClient();
        var findings = await SecurityBuiltins.RunAllAsync(upstream.Urls.First(), http, new List<string>(), ct);

        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Cwe == "CWE-319");
        var plain = findings.Single(f => f.Template.Recording.Vulnerability?.Cwe == "CWE-319");
        Assert.Equal(ScanFindingStatus.Vulnerable, plain.Status);
    }

    [Fact]
    public async Task RunAllAsync_MalformedTargetUrl_EmitsErrorFinding()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        // Unclosed IPv6 literal → UriFormatException in
        // TlsVersionEnumeration's `new Uri(...)`. All three sub-checks
        // recover gracefully (TlsEnumeration via UriFormatException
        // guard; BannerDisclosure via blanket catch; VerboseError via
        // widened catch covering the URL-parse path).
        var findings = await SecurityBuiltins.RunAllAsync("http://[unclosed", http, new List<string>(), ct);
        Assert.Contains(findings, f => f.Status == ScanFindingStatus.Error);
    }

[Fact]
    public async Task RunAllAsync_DiscloseServerHeader_EmitsBannerFinding()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(async ctx =>
        {
            ctx.Response.Headers["X-Powered-By"] = "Express";
            ctx.Response.Headers["X-AspNet-Version"] = "4.0.30319";
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("hello", ctx.RequestAborted);
        }, ct);
        using var http = new HttpClient();

        var findings = await SecurityBuiltins.RunAllAsync(upstream.Urls.First(), http, new List<string>(), ct);

        Assert.Contains(findings, f => (f.Template.Recording.Vulnerability?.Id ?? "").Contains("BANNER", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Detail.Contains("X-Powered-By", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAllAsync_VerboseErrorBody_EmitsCwe209Finding()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(async ctx =>
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "text/html";
            await ctx.Response.WriteAsync(
                "<html><body><h2>Whitelabel Error Page</h2>"
                + "<pre>at MyApp.Controllers.LoginController.Authenticate() in /src/Login.cs:line 42</pre>"
                + "<p>System.NullReferenceException: Object reference not set to an instance of an object.</p>"
                + "</body></html>",
                ctx.RequestAborted);
        }, ct);
        using var http = new HttpClient();

        var findings = await SecurityBuiltins.RunAllAsync(upstream.Urls.First(), http, new List<string>(), ct);
        Assert.Contains(findings, f => f.Template.Recording.Vulnerability?.Cwe == "CWE-209");
    }

    [Fact]
    public async Task RunAllAsync_AuthHeadersApplied_ReachAuthenticatedTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        string? seenAuth = null;
        await using var upstream = await StartUpstreamAsync(async ctx =>
        {
            if (ctx.Request.Headers.TryGetValue("Authorization", out var v)) seenAuth = v.ToString();
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("hello", ctx.RequestAborted);
        }, ct);

        using var http = new HttpClient();
        await SecurityBuiltins.RunAllAsync(upstream.Urls.First(), http, new[] { "Authorization: Bearer abc" }, ct);
        Assert.Equal("Bearer abc", seenAuth);
    }

    [Fact]
    public async Task RunAllAsync_HttpsTarget_RunsTlsVersionEnumeration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartHttpsUpstreamAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("hi", ctx.RequestAborted);
        }, ct);
        var url = upstream.Urls.First();

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var http = new HttpClient(handler);
        var findings = await SecurityBuiltins.RunAllAsync(url, http, new List<string>(), ct);

        // The enumeration ran against all four protocols — each one
        // produces a finding either way (accepted/Vulnerable for
        // legacy TLS, accepted/Safe for modern, or rejected/Safe).
        // We just verify that the path was driven, not the exact mix
        // (Kestrel's default SslProtocols depends on the OS).
        var tlsFindings = findings.Where(f => (f.Template.Recording.Vulnerability?.Id ?? "").StartsWith("BWR-BUILTIN-TLS-TLS", StringComparison.Ordinal)).ToList();
        Assert.True(tlsFindings.Count >= 2, $"Expected ≥2 TLS-protocol findings, got {tlsFindings.Count}");
    }

    [Fact]
    public async Task RunAllAsync_UnreachableHttpsTarget_EmitsErrorFinding()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        // Black-hole HTTPS target: localhost:1 won't accept connections.
        // TlsVersionEnumeration loops through all 4 protocols, every
        // connect fails — emits the "Could not reach …" error finding.
        var findings = await SecurityBuiltins.RunAllAsync("https://127.0.0.1:1", http, new List<string>(), ct);
        Assert.Contains(findings, f => f.Status == ScanFindingStatus.Error
            && f.Detail.Contains("Could not reach", StringComparison.Ordinal));
    }

    private static async Task<WebApplication> StartHttpsUpstreamAsync(RequestDelegate handler, CancellationToken ct)
    {
        // Self-signed cert for the upstream's HTTPS listener.
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=localhost", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var sanBuilder = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(sanBuilder.Build());
        using var raw = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7));
        var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            raw.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12),
            password: null,
            keyStorageFlags: System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l =>
        {
            l.Protocols = HttpProtocols.Http1;
            l.UseHttps(new Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions { ServerCertificate = cert });
        }));
        var app = builder.Build();
        ((IApplicationBuilder)app).Run(handler);
        await app.StartAsync(ct);
        return app;
    }

    private static async Task<WebApplication> StartUpstreamAsync(RequestDelegate handler, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        ((IApplicationBuilder)app).Run(handler);
        await app.StartAsync(ct);
        return app;
    }
}
