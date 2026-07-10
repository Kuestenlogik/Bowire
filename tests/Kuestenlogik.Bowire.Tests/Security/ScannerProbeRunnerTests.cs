// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Security;
using Kuestenlogik.Bowire.Security.Scanner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// End-to-end coverage for the scanner-backed <see cref="ISecurityScanProbeRunner"/>
/// (#104): it runs the real HTTP OWASP probes against an endpoint URL and maps
/// vulnerable findings into orchestrator findings.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
public sealed class ScannerProbeRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunAsync_AgainstCorsReflectingServer_MapsVulnerableFinding()
    {
        await using var up = await StartCorsReflectingAsync(Ct);
        var runner = new ScannerProbeRunner();

        var findings = await runner.RunAsync(new OrchestratorEndpoint("e1", "/"), up.Urls.First(), Ct);

        // The API8 CORS-reflect-credentials probe fires on this server.
        var f = Assert.Single(findings, x => x.RuleId == "BWR-OWASP-API8-CORS-REFLECT-CREDS");
        Assert.Equal("e1", f.EndpointId);
        Assert.StartsWith("API8", f.OwaspApi!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NonUrlTarget_ReturnsEmpty()
    {
        var runner = new ScannerProbeRunner();
        Assert.Empty(await runner.RunAsync(new OrchestratorEndpoint("e1", "/"), "not-a-url", Ct));
    }

    // Reflects any Origin back with Access-Control-Allow-Credentials=true — the
    // classic CORS misconfiguration the API8 probe flags.
    private static async Task<WebApplication> StartCorsReflectingAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.Run(ctx =>
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin))
            {
                ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
                ctx.Response.Headers["Access-Control-Allow-Credentials"] = "true";
            }
            ctx.Response.StatusCode = 200;
            return ctx.Response.WriteAsync("ok", ctx.RequestAborted);
        });
        await app.StartAsync(ct);
        return app;
    }
}
