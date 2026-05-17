// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using Kuestenlogik.Bowire.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Proxy;

/// <summary>
/// End-to-end smoke test for <see cref="BowireProxyServer"/> — stands
/// up an in-process upstream HTTP server + the proxy on dynamic ports,
/// then verifies that a request forwarded through the proxy lands on
/// the upstream AND gets captured into the <see cref="CapturedFlowStore"/>
/// with the expected method / URL / body / status fields.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList",
    Justification = "Loopback-only test traffic against an in-process Kestrel; no CRL endpoint to query.")]
public sealed class BowireProxyServerTests
{
    [Fact]
    public async Task ForwardsRequestAndCapturesFlow()
    {
        var ct = TestContext.Current.CancellationToken;

        // Upstream: echoes the request body back with a known status + custom header.
        await using var upstream = await StartUpstreamAsync(ct);
        var upstreamUrl = upstream.Urls.First();

        var store = new CapturedFlowStore();
        await using var proxy = new BowireProxyServer(store, port: 0);
        await proxy.StartAsync(ct);
        Assert.True(proxy.Port > 0);

        // Proxy-aware client: routes via the Bowire proxy.
        var proxyAddress = new Uri($"http://127.0.0.1:{proxy.Port}");
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(proxyAddress),
            UseProxy = true,
        };
        using var http = new HttpClient(handler);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{upstreamUrl.TrimEnd('/')}/echo")
        {
            Content = new StringContent("{\"hello\":\"world\"}", Encoding.UTF8, "application/json"),
        };

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Contains("hello", body, StringComparison.Ordinal);
        Assert.True(resp.Headers.TryGetValues("X-Bowire-Test", out var hv) && hv.Single() == "yes");

        // Flow captured?
        var snap = store.Snapshot();
        Assert.Single(snap);
        var flow = snap[0];
        Assert.Equal("POST", flow.Method);
        Assert.Contains("/echo", flow.Url, StringComparison.Ordinal);
        Assert.Equal(201, flow.ResponseStatus);
        Assert.Contains("hello", flow.RequestBody ?? "", StringComparison.Ordinal);
        Assert.Contains("hello", flow.ResponseBody ?? "", StringComparison.Ordinal);
        Assert.Null(flow.Error);
    }

    [Fact]
    public async Task ConnectMethod_RejectedWith501InStageA()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new CapturedFlowStore();
        await using var proxy = new BowireProxyServer(store, port: 0);
        await proxy.StartAsync(ct);

        using var handler = new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler);
        using var req = new HttpRequestMessage(new HttpMethod("CONNECT"), $"http://127.0.0.1:{proxy.Port}/");
        req.Headers.Host = "example.com:443";

        using var resp = await http.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
    }

    private static async Task<WebApplication> StartUpstreamAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1);
        });
        var app = builder.Build();
        app.MapPost("/echo", async (HttpContext ctx) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            ctx.Response.Headers["X-Bowire-Test"] = "yes";
            ctx.Response.StatusCode = 201;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(ms.ToArray(), ctx.RequestAborted);
        });
        await app.StartAsync(cancellationToken);
        return app;
    }
}
