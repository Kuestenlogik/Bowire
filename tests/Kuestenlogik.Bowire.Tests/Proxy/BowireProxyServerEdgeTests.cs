// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Kuestenlogik.Bowire.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Proxy;

/// <summary>
/// Edge-case coverage for <see cref="BowireProxyServer"/> — the
/// happy-path E2E lives in <see cref="BowireProxyServerTests"/>; this
/// file drives raw-socket protocol-level oddities (malformed request
/// lines, missing Host header, repeated headers, status-reason
/// table) that need direct TCP traffic instead of HttpClient. Every
/// test bypasses HttpClient so it can craft bytes the .NET stack
/// would otherwise normalise.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class BowireProxyServerEdgeTests
{
    private static async Task<string> SendRawAndReadAsync(int port, string request, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        await stream.FlushAsync(ct);
        // The proxy closes the socket aggressively after a 4xx/5xx
        // (Connection: close). On Windows that occasionally surfaces
        // as RST instead of FIN, which throws IOException out of
        // CopyToAsync — read until that fires and return whatever
        // arrived first.
        using var ms = new MemoryStream();
        try { await stream.CopyToAsync(ms, ct); }
        catch (IOException) { /* peer closed mid-stream — partial read still useful */ }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    [Fact]
    public async Task HandlesMalformedRequestLine_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new CapturedFlowStore();
        await using var proxy = new BowireProxyServer(store, port: 0);
        await proxy.StartAsync(ct);

        var response = await SendRawAndReadAsync(proxy.Port, "GIBBERISH\r\n\r\n", ct);
        Assert.Contains("400", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlesOriginFormRequestWithoutHostHeader_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new CapturedFlowStore();
        await using var proxy = new BowireProxyServer(store, port: 0);
        await proxy.StartAsync(ct);

        // Origin-form (no scheme://host in the request-target) + no
        // Host header — proxy can't derive an upstream, must reject.
        var response = await SendRawAndReadAsync(proxy.Port, "GET /foo HTTP/1.1\r\n\r\n", ct);
        Assert.Contains("400", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlesMalformedConnectLine_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new CapturedFlowStore();
        await using var proxy = new BowireProxyServer(store, port: 0);
        await proxy.StartAsync(ct);

        var response = await SendRawAndReadAsync(proxy.Port, "CONNECT\r\n\r\n", ct);
        Assert.Contains("400", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedHeader_CombinedIntoSingleCommaSeparatedValue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(async ctx =>
        {
            // Echo the X-Test-Multi values we see in the captured flow.
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("ok", ctx.RequestAborted);
        }, ct);
        var upstreamUri = new Uri(upstream.Urls.First());

        var store = new CapturedFlowStore();
        await using var proxy = new BowireProxyServer(store, port: 0);
        await proxy.StartAsync(ct);

        var requestLine =
            $"GET / HTTP/1.1\r\n" +
            $"Host: {upstreamUri.Host}:{upstreamUri.Port}\r\n" +
            "X-Test-Multi: first\r\n" +
            "X-Test-Multi: second\r\n" +
            "\r\n";
        await SendRawAndReadAsync(proxy.Port, requestLine, ct);

        var snap = store.Snapshot();
        Assert.Single(snap);
        var combined = snap[0].RequestHeaders.Single(h => h.Key == "X-Test-Multi").Value;
        Assert.Equal("first, second", combined);
    }

    [Theory]
    [InlineData(204)]
    [InlineData(301)]
    [InlineData(304)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task StatusReason_ForwardsUpstreamStatusCodeVerbatim(int upstreamStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(async ctx =>
        {
            ctx.Response.StatusCode = upstreamStatus;
            if (upstreamStatus != 204 && upstreamStatus != 304)
                await ctx.Response.WriteAsync("x", ctx.RequestAborted);
        }, ct);
        var upstreamUri = new Uri(upstream.Urls.First());

        var store = new CapturedFlowStore();
        await using var proxy = new BowireProxyServer(store, port: 0);
        await proxy.StartAsync(ct);

        var proxyAddress = new Uri($"http://127.0.0.1:{proxy.Port}");
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxyAddress), UseProxy = true };
        using var http = new HttpClient(handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, upstreamUri);
        using var resp = await http.SendAsync(req, ct);
        Assert.Equal((HttpStatusCode)upstreamStatus, resp.StatusCode);
    }

    [Fact]
    public async Task ContentLengthZero_StillForwardsRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("zero-cl", ctx.RequestAborted);
        }, ct);
        var upstreamUri = new Uri(upstream.Urls.First());

        var store = new CapturedFlowStore();
        await using var proxy = new BowireProxyServer(store, port: 0);
        await proxy.StartAsync(ct);

        var raw =
            $"POST / HTTP/1.1\r\n" +
            $"Host: {upstreamUri.Host}:{upstreamUri.Port}\r\n" +
            "Content-Length: 0\r\n\r\n";
        var resp = await SendRawAndReadAsync(proxy.Port, raw, ct);
        Assert.Contains("zero-cl", resp, StringComparison.Ordinal);

        var snap = store.Snapshot();
        Assert.Single(snap);
        Assert.Null(snap[0].RequestBody);
    }

    [Fact]
    public async Task ContentLengthNegative_IgnoresBody()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("ok", ctx.RequestAborted);
        }, ct);
        var upstreamUri = new Uri(upstream.Urls.First());

        var store = new CapturedFlowStore();
        await using var proxy = new BowireProxyServer(store, port: 0);
        await proxy.StartAsync(ct);

        var raw =
            $"POST / HTTP/1.1\r\n" +
            $"Host: {upstreamUri.Host}:{upstreamUri.Port}\r\n" +
            "Content-Length: -1\r\n\r\n";
        var resp = await SendRawAndReadAsync(proxy.Port, raw, ct);
        Assert.Contains("200", resp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentLengthGarbage_IgnoresBody()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await StartUpstreamAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("ok", ctx.RequestAborted);
        }, ct);
        var upstreamUri = new Uri(upstream.Urls.First());

        var store = new CapturedFlowStore();
        await using var proxy = new BowireProxyServer(store, port: 0);
        await proxy.StartAsync(ct);

        var raw =
            $"POST / HTTP/1.1\r\n" +
            $"Host: {upstreamUri.Host}:{upstreamUri.Port}\r\n" +
            "Content-Length: abc\r\n\r\n";
        var resp = await SendRawAndReadAsync(proxy.Port, raw, ct);
        Assert.Contains("200", resp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImmediateClientDisconnect_NoCrash()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new CapturedFlowStore();
        await using var proxy = new BowireProxyServer(store, port: 0);
        await proxy.StartAsync(ct);

        // Open + close immediately. The proxy's accept loop must not
        // crash on an empty connection.
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, proxy.Port, ct);
            // Close without sending anything.
        }

        // Proxy should still be alive — send a real request.
        await using var upstream = await StartUpstreamAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("alive", ctx.RequestAborted);
        }, ct);
        var upstreamUri = new Uri(upstream.Urls.First());
        var proxyAddress = new Uri($"http://127.0.0.1:{proxy.Port}");
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxyAddress), UseProxy = true };
        using var http = new HttpClient(handler);
        var body = await http.GetStringAsync(upstreamUri, ct);
        Assert.Equal("alive", body);
    }

    [Fact]
    public async Task StopAsync_BeforeStart_NoOp()
    {
        var store = new CapturedFlowStore();
        await using var proxy = new BowireProxyServer(store, port: 0);
        // Never call StartAsync; StopAsync should still return cleanly.
        await proxy.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Constructor_PortOutOfRange_Throws()
    {
        var store = new CapturedFlowStore();
        Assert.Throws<ArgumentOutOfRangeException>(() => new BowireProxyServer(store, port: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BowireProxyServer(store, port: 70000));
    }

    [Fact]
    public void Constructor_NullStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new BowireProxyServer(null!, port: 0));
    }

    [Fact]
    public async Task HttpsInterceptionEnabled_ReflectsCaPresence()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new CapturedFlowStore();
        await using var proxyNoCa = new BowireProxyServer(store, port: 0, ca: null);
        Assert.False(proxyNoCa.HttpsInterceptionEnabled);

        var caDir = Path.Combine(Path.GetTempPath(), $"bowire-mitm-flag-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(caDir);
        try
        {
            using var ca = BowireProxyCertificateAuthority.LoadOrCreate(caDir);
            await using var proxyWithCa = new BowireProxyServer(store, port: 0, ca);
            Assert.True(proxyWithCa.HttpsInterceptionEnabled);
            await proxyWithCa.StartAsync(ct);
        }
        finally { Directory.Delete(caDir, recursive: true); }
    }

    private static async Task<WebApplication> StartUpstreamAsync(RequestDelegate handler, CancellationToken ct)
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
