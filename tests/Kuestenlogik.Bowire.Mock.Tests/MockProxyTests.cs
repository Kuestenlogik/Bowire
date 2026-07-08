// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// #407: selective upstream proxy — forward-on-miss + per-stub proxy, driven
/// against a real loopback Kestrel upstream (the mock's outbound HttpClient
/// hits it for real).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
public sealed class MockProxyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ForwardOnMiss_UnmatchedRequest_HitsUpstream_MatchedStub_Wins()
    {
        await using var upstream = await StartUpstreamAsync(Ct);
        var upstreamUrl = upstream.Urls.First();

        var rec = new BowireRecording
        {
            Id = "rec_proxy", Name = "proxy", RecordingFormatVersion = 2,
            Steps =
            {
                new BowireRecordingStep
                {
                    Id = "mocked", Protocol = "rest", Service = "S", Method = "M", MethodType = "Unary",
                    HttpPath = "/mocked", HttpVerb = "GET", Status = "OK", Response = """{"from":"mock"}""",
                },
            },
        };

        using var host = BuildMockHost(rec, proxyBaseUrl: upstreamUrl);
        var client = host.GetTestClient();

        // Matched stub → mock's recorded response (mock shadows the upstream).
        Assert.Equal("""{"from":"mock"}""",
            await (await client.GetAsync(new Uri("/mocked", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct));

        // Unmatched → forwarded to the real upstream, which echoes the path.
        var proxied = await client.GetAsync(new Uri("/live/data", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, proxied.StatusCode);
        Assert.Equal("""{"upstream":"/live/data"}""", await proxied.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task PerStubProxy_MatchedStub_ForwardsInsteadOfReplaying()
    {
        await using var upstream = await StartUpstreamAsync(Ct);
        var upstreamUrl = upstream.Urls.First();

        var rec = new BowireRecording
        {
            Id = "rec_proxy2", Name = "proxy2", RecordingFormatVersion = 2,
            Steps =
            {
                new BowireRecordingStep
                {
                    Id = "viaproxy", Protocol = "rest", Service = "S", Method = "M", MethodType = "Unary",
                    HttpPath = "/viaproxy", HttpVerb = "GET", Status = "OK",
                    Response = """{"never":"served"}""",
                    Proxy = upstreamUrl, // #407: this stub proxies instead of replaying
                },
            },
        };

        using var host = BuildMockHost(rec, proxyBaseUrl: null);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/viaproxy", UriKind.Relative), Ct);
        Assert.Equal("""{"upstream":"/viaproxy"}""", await resp.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ForwardOnMiss_UnreachableUpstream_Returns502()
    {
        var rec = new BowireRecording { Id = "r", Name = "r", RecordingFormatVersion = 2 };
        // Port 1 is not listening → HttpRequestException → 502.
        using var host = BuildMockHost(rec, proxyBaseUrl: "http://127.0.0.1:1");
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/anything", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    private static IHost BuildMockHost(BowireRecording recording, string? proxyBaseUrl) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                    .Configure(app =>
                    {
                        app.UseBowireMock(recording, opts =>
                        {
                            opts.Watch = false;
                            opts.ReplaySpeed = 0;
                            opts.ProxyBaseUrl = proxyBaseUrl;
                        });
                        app.Run(async ctx =>
                        {
                            ctx.Response.StatusCode = 418;
                            await ctx.Response.WriteAsync("fallthrough");
                        });
                    });
            })
            .Start();

    private static async Task<WebApplication> StartUpstreamAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.Run(async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($$"""{"upstream":"{{ctx.Request.Path.Value}}"}""", ctx.RequestAborted);
        });
        await app.StartAsync(ct);
        return app;
    }
}
