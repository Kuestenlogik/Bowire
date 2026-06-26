// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using Kuestenlogik.Bowire.Interceptor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Interceptor;

/// <summary>
/// Coverage for <see cref="BowireReverseProxyHost"/> — the standalone
/// reverse-proxy mode (#307 / Phase C). Spins up a real upstream Kestrel
/// host with a couple of endpoints, fronts it with the reverse-proxy
/// host, and asserts the captured flows match the actual round-trip.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class BowireReverseProxyHostTests
{
    private static async Task<WebApplication> StartUpstreamAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.MapGet("/api/hello", () => Results.Ok(new { greeting = "hi" }));
        app.MapPost("/api/echo", async (HttpContext ctx) =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            var text = await sr.ReadToEndAsync(ctx.RequestAborted);
            return Results.Text(text, "application/json");
        });
        app.MapGet("/api/stream", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("data: tick\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        });
        await app.StartAsync(ct);
        return app;
    }

    [Fact]
    public async Task ForwardsGetRequest_AndRecordsFlow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);
        var upstreamUrl = new Uri(upstream.Urls.First());

        await using var edge = BowireReverseProxyHost.Create(new BowireReverseProxyHostOptions
        {
            Upstream = upstreamUrl,
            ListenAddress = IPAddress.Loopback,
            ListenPort = 0,
            Capacity = 50,
        });
        await edge.StartAsync(ct);
        Assert.True(edge.EdgePort > 0);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{edge.EdgePort}") };
        using var resp = await http.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("hi", body, StringComparison.Ordinal);

        var snap = edge.Store.Snapshot();
        Assert.Single(snap);
        Assert.Equal("GET", snap[0].Method);
        Assert.Equal(200, snap[0].ResponseStatus);
        Assert.Contains("/api/hello", snap[0].Path, StringComparison.Ordinal);
        Assert.False(snap[0].Streaming);
    }

    [Fact]
    public async Task ForwardsPostRequest_WithBody_AndRecordsBothSides()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);
        var upstreamUrl = new Uri(upstream.Urls.First());

        await using var edge = BowireReverseProxyHost.Create(new BowireReverseProxyHostOptions
        {
            Upstream = upstreamUrl,
            Capacity = 10,
        });
        await edge.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{edge.EdgePort}") };
        using var content = new StringContent("""{"name":"unit-test"}""", Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(new Uri("/api/echo", UriKind.Relative), content, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("unit-test", body, StringComparison.Ordinal);

        var flow = edge.Store.Snapshot().Single();
        Assert.Equal("POST", flow.Method);
        Assert.Equal(200, flow.ResponseStatus);
        Assert.NotNull(flow.RequestBody);
        Assert.Contains("unit-test", flow.RequestBody!, StringComparison.Ordinal);
        Assert.NotNull(flow.ResponseBody);
        Assert.Contains("unit-test", flow.ResponseBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetectsStreamingResponse_AndRecordsWithStreamingFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(ct);
        var upstreamUrl = new Uri(upstream.Urls.First());

        await using var edge = BowireReverseProxyHost.Create(new BowireReverseProxyHostOptions
        {
            Upstream = upstreamUrl,
            Capacity = 10,
        });
        await edge.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{edge.EdgePort}") };
        using var resp = await http.GetAsync(new Uri("/api/stream", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var flow = edge.Store.Snapshot().Single();
        Assert.True(flow.Streaming);
        // Streaming responses have empty body fields in the rail.
        Assert.Null(flow.ResponseBody);
    }

    [Fact]
    public async Task UnreachableUpstream_Records502_AndErrorMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        // 198.51.100.0/24 (TEST-NET-2, RFC 5737) is documentation-only and
        // never globally routable — a connect attempt should fail fast.
        var upstream = new Uri("http://198.51.100.1:1");

        await using var edge = BowireReverseProxyHost.Create(new BowireReverseProxyHostOptions
        {
            Upstream = upstream,
            Capacity = 10,
            UpstreamTimeout = TimeSpan.FromSeconds(3),
        });
        await edge.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{edge.EdgePort}") };
        // The forward will fail (HttpRequestException) and the middleware
        // writes a 502 + records the error. A 200 here would indicate the
        // forward unexpectedly succeeded.
        using var resp = await http.GetAsync(new Uri("/api/anything", UriKind.Relative), ct);

        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
        var flow = edge.Store.Snapshot().Single();
        Assert.NotNull(flow.Error);
    }

    [Fact]
    public void Create_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BowireReverseProxyHost.Create(null!));
    }

    [Fact]
    public void Create_MissingUpstream_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            BowireReverseProxyHost.Create(new BowireReverseProxyHostOptions { Upstream = null }));
        Assert.Contains("Upstream", ex.Message, StringComparison.Ordinal);
    }

}
