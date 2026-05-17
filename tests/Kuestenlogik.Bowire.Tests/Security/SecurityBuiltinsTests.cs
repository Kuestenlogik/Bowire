// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.App;
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
        var findings = await SecurityBuiltins.RunAllAsync("::not a url::", http, new List<string>(), ct);
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
