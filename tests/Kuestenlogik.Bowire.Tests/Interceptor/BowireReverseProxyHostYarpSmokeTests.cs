// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Kuestenlogik.Bowire.Interceptor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Interceptor;

/// <summary>
/// YARP-migration smoke coverage for <see cref="BowireReverseProxyHost"/>
/// (#323). The host-level tests in
/// <see cref="BowireReverseProxyHostTests"/> already cover GET / POST /
/// streaming / 502-on-unreachable; this file pins the YARP-equivalent
/// behaviours that are easy to silently regress when swapping the
/// forwarder out:
/// <list type="bullet">
///   <item>request bodies arrive at the upstream <em>verbatim</em>
///   (every byte) and the Content-Type header survives the
///   transformer's header-copy seam;</item>
///   <item>response bodies stream back to the client without YARP's
///   buffered-HttpClient path swallowing bytes — the client sees the
///   same payload size the upstream emitted;</item>
///   <item>arbitrary request headers (e.g. <c>Authorization</c>,
///   <c>X-Custom</c>) round-trip to the upstream;</item>
///   <item>query strings join correctly onto the upstream URI prefix.</item>
/// </list>
/// </summary>
/// <remarks>
/// These tests intentionally exercise the host's public surface only
/// (Create + StartAsync) — the YARP <see cref="Yarp.ReverseProxy.Forwarder.IHttpForwarder"/>,
/// <see cref="Yarp.ReverseProxy.Forwarder.HttpTransformer"/>, and
/// <see cref="System.Net.Http.HttpMessageInvoker"/> live behind the
/// host. If the forwarder ever swaps again, this suite still holds.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class BowireReverseProxyHostYarpSmokeTests
{
    private static async Task<(WebApplication app, Uri url)> StartUpstreamAsync(
        Action<WebApplication> configure,
        CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        configure(app);
        await app.StartAsync(ct);
        return (app, new Uri(app.Urls.First()));
    }

    [Fact]
    public async Task PostBody_ArrivesAtUpstreamVerbatim_WithContentType()
    {
        var ct = TestContext.Current.CancellationToken;
        string? receivedBody = null;
        string? receivedContentType = null;
        var (upstream, upstreamUrl) = await StartUpstreamAsync(app =>
        {
            app.MapPost("/api/echo", async (HttpContext ctx) =>
            {
                receivedContentType = ctx.Request.ContentType;
                using var sr = new StreamReader(ctx.Request.Body);
                receivedBody = await sr.ReadToEndAsync(ctx.RequestAborted);
                return Results.Text("ok");
            });
        }, ct);
        await using var _upstream = upstream;

        await using var edge = BowireReverseProxyHost.Create(new BowireReverseProxyHostOptions
        {
            Upstream = upstreamUrl,
            Capacity = 10,
        });
        await edge.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{edge.EdgePort}") };
        var payload = """{"value":"hello, YARP!"}""";
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(new Uri("/api/echo", UriKind.Relative), content, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(payload, receivedBody);
        Assert.NotNull(receivedContentType);
        Assert.Contains("application/json", receivedContentType!, StringComparison.Ordinal);

        // Capture seam still records the request body.
        var flow = edge.Store.Snapshot().Single();
        Assert.Equal(payload, flow.RequestBody);
    }

    [Fact]
    public async Task ResponseBody_StreamsBackVerbatim_AndIsCapturedForRail()
    {
        var ct = TestContext.Current.CancellationToken;
        // 32 KiB of deterministic-but-non-trivial data: forces YARP's
        // streaming-aware response copy (HttpClient buffers responses
        // by default; HttpMessageInvoker doesn't — this guards against
        // a regression that swaps invoker → client).
        var expectedBytes = new byte[32 * 1024];
        for (var i = 0; i < expectedBytes.Length; i++) expectedBytes[i] = (byte)(i % 251);

        var (upstream, upstreamUrl) = await StartUpstreamAsync(app =>
        {
            app.MapGet("/blob", (HttpContext ctx) =>
            {
                ctx.Response.ContentType = "application/octet-stream";
                return ctx.Response.Body.WriteAsync(expectedBytes, ctx.RequestAborted).AsTask();
            });
        }, ct);
        await using var _upstream = upstream;

        await using var edge = BowireReverseProxyHost.Create(new BowireReverseProxyHostOptions
        {
            Upstream = upstreamUrl,
            Capacity = 10,
            MaxBodyBytes = 1024 * 1024, // 1 MiB cap — well above payload, no truncation expected.
        });
        await edge.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{edge.EdgePort}") };
        using var resp = await http.GetAsync(new Uri("/blob", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var actualBytes = await resp.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal(expectedBytes.Length, actualBytes.Length);
        Assert.True(expectedBytes.AsSpan().SequenceEqual(actualBytes), "Response body should round-trip verbatim.");

        // Captured side (binary classifier kicks in once we hit a NUL).
        var flow = edge.Store.Snapshot().Single();
        Assert.False(flow.ResponseBodyTruncated);
        Assert.NotNull(flow.ResponseBodyBase64);
    }

    [Fact]
    public async Task CustomRequestHeaders_RoundTripToUpstream()
    {
        var ct = TestContext.Current.CancellationToken;
        string? receivedAuth = null;
        string? receivedCustom = null;
        var (upstream, upstreamUrl) = await StartUpstreamAsync(app =>
        {
            app.MapGet("/api/whoami", (HttpContext ctx) =>
            {
                receivedAuth = ctx.Request.Headers.Authorization.ToString();
                receivedCustom = ctx.Request.Headers["X-Bowire-Test"].ToString();
                return Results.Ok();
            });
        }, ct);
        await using var _upstream = upstream;

        await using var edge = BowireReverseProxyHost.Create(new BowireReverseProxyHostOptions
        {
            Upstream = upstreamUrl,
            Capacity = 10,
        });
        await edge.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{edge.EdgePort}") };
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/whoami", UriKind.Relative));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "abc.def.ghi");
        req.Headers.TryAddWithoutValidation("X-Bowire-Test", "yarp-smoke");
        using var resp = await http.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("Bearer abc.def.ghi", receivedAuth);
        Assert.Equal("yarp-smoke", receivedCustom);
    }

    [Fact]
    public async Task QueryString_Joins_OntoUpstreamPath()
    {
        var ct = TestContext.Current.CancellationToken;
        string? receivedPath = null;
        string? receivedQuery = null;
        var (upstream, upstreamUrl) = await StartUpstreamAsync(app =>
        {
            app.MapGet("/api/search", (HttpContext ctx) =>
            {
                receivedPath = ctx.Request.Path;
                receivedQuery = ctx.Request.QueryString.Value;
                return Results.Ok();
            });
        }, ct);
        await using var _upstream = upstream;

        await using var edge = BowireReverseProxyHost.Create(new BowireReverseProxyHostOptions
        {
            Upstream = upstreamUrl,
            Capacity = 10,
        });
        await edge.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{edge.EdgePort}") };
        using var resp = await http.GetAsync(new Uri("/api/search?q=hello&page=2", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("/api/search", receivedPath);
        Assert.Equal("?q=hello&page=2", receivedQuery);

        var flow = edge.Store.Snapshot().Single();
        Assert.Contains("q=hello", flow.Url, StringComparison.Ordinal);
    }
}
