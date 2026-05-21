// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// HTTP binding resolver coverage. Unlike Kafka / WebSocket the
/// HTTP resolver doesn't dispatch to a Bowire wire plugin — it
/// drives HttpClient directly. Tests stand up a real, throwaway
/// HttpListener on an OS-picked port so the resolver actually hits
/// the wire end-to-end. Avoids the brittleness of a mocked
/// HttpMessageHandler while still running offline (no Docker, no
/// outbound network).
/// </summary>
public sealed class HttpBindingResolverTests : IAsyncLifetime
{
    private HttpListener? _listener;
    private int _port;
    private readonly List<(HttpMethod Method, string Path, string Body, Dictionary<string, string> Headers)> _received = [];

    public async ValueTask InitializeAsync()
    {
        _port = FindFreeTcpPort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();

        // Drain requests on a background loop — each one is parsed
        // into _received so individual tests can inspect what the
        // resolver actually sent. Pre-canned 200 response with a
        // small JSON body so the resolver gets something to read.
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }

                var method = new HttpMethod(ctx.Request.HttpMethod);
                var path = ctx.Request.Url?.PathAndQuery ?? string.Empty;
                using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
                var body = await reader.ReadToEndAsync();

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string h in ctx.Request.Headers)
                {
                    headers[h] = ctx.Request.Headers[h] ?? string.Empty;
                }
                _received.Add((method, path, body, headers));

                var response = ctx.Response;
                var buffer = Encoding.UTF8.GetBytes("""{"echo":"ok"}""");
                response.ContentLength64 = buffer.Length;
                response.ContentType = "application/json";
                response.StatusCode = 200;
                await response.OutputStream.WriteAsync(buffer);
                response.OutputStream.Close();
            }
        });

        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _listener?.Stop();
        _listener?.Close();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task InvokeAsync_DefaultsSendToPost_WithJsonBody()
    {
        var resolver = new HttpBindingResolver();
        var context = new AsyncApiChannelContext(
            ServerUrl: $"http://127.0.0.1:{_port}",
            ChannelAddress: "/events",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>());

        var result = await resolver.InvokeAsync(
            context,
            jsonMessages: ["""{"hello":"world"}"""],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Single(_received);
        Assert.Equal(HttpMethod.Post, _received[0].Method);
        Assert.Equal("/events", _received[0].Path);
        Assert.Equal("""{"hello":"world"}""", _received[0].Body);
        Assert.Equal("application/json; charset=utf-8", _received[0].Headers["Content-Type"]);
    }

    [Fact]
    public async Task InvokeAsync_DefaultsReceiveToGet_WithoutBody()
    {
        var resolver = new HttpBindingResolver();
        var context = new AsyncApiChannelContext(
            ServerUrl: $"http://127.0.0.1:{_port}",
            ChannelAddress: "/feed",
            OperationAction: "receive",
            BindingFields: new Dictionary<string, string>());

        await resolver.InvokeAsync(
            context,
            jsonMessages: [],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Single(_received);
        Assert.Equal(HttpMethod.Get, _received[0].Method);
        Assert.Empty(_received[0].Body);
    }

    [Fact]
    public async Task InvokeAsync_HonoursDocVerbOverride()
    {
        var resolver = new HttpBindingResolver();
        var context = new AsyncApiChannelContext(
            ServerUrl: $"http://127.0.0.1:{_port}",
            ChannelAddress: "/items/42",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["method"] = "PUT"
            });

        await resolver.InvokeAsync(
            context,
            jsonMessages: ["""{"updated":true}"""],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, _received[0].Method);
    }

    [Fact]
    public async Task InvokeAsync_CallerMetadataVerbOverridesDocBinding()
    {
        var resolver = new HttpBindingResolver();
        var context = new AsyncApiChannelContext(
            ServerUrl: $"http://127.0.0.1:{_port}",
            ChannelAddress: "/items",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["method"] = "POST"
            });

        // Caller wants DELETE one-off instead of the doc's POST.
        await resolver.InvokeAsync(
            context,
            jsonMessages: [],
            metadata: new Dictionary<string, string> { ["method"] = "DELETE" },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, _received[0].Method);
    }

    [Fact]
    public async Task InvokeAsync_ForwardsCustomHeaders_SkipsBowireReserved()
    {
        var resolver = new HttpBindingResolver();
        var context = new AsyncApiChannelContext(
            ServerUrl: $"http://127.0.0.1:{_port}",
            ChannelAddress: "/secure",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>());

        await resolver.InvokeAsync(
            context,
            jsonMessages: ["{}"],
            metadata: new Dictionary<string, string>
            {
                ["X-Auth-Token"] = "abc123",
                ["X-Bowire-Subprotocol"] = "should-not-leak", // reserved → skipped
                ["__bowireGrpcTransport"] = "web"             // reserved → skipped
            },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("abc123", _received[0].Headers["X-Auth-Token"]);
        Assert.False(_received[0].Headers.ContainsKey("X-Bowire-Subprotocol"));
        Assert.False(_received[0].Headers.ContainsKey("__bowireGrpcTransport"));
    }

    [Fact]
    public async Task InvokeAsync_NonSuccessStatus_MapsToHttpStatusInResult()
    {
        // Stand up a one-shot listener on a fresh port that always
        // replies 503 — pins the resolver's status-mapping behaviour
        // without polluting the shared fixture listener's expected-
        // 200 responses.
        var failPort = FindFreeTcpPort();
        using var failListener = new HttpListener();
        failListener.Prefixes.Add($"http://127.0.0.1:{failPort}/");
        failListener.Start();
        // HttpListener.GetContextAsync() doesn't take a CancellationToken
        // (the API predates the pattern), so xUnit1051 fires here despite
        // the surrounding test honouring TestContext.Current.CancellationToken
        // on the resolver call below. The listener is disposed in the
        // test's finally path, which ends the pending accept either way.
#pragma warning disable xUnit1051
        var serverTask = Task.Run(async () =>
        {
            var ctx = await failListener.GetContextAsync();
            ctx.Response.StatusCode = 503;
            ctx.Response.OutputStream.Close();
        });
#pragma warning restore xUnit1051

        var resolver = new HttpBindingResolver();
        var context = new AsyncApiChannelContext(
            ServerUrl: $"http://127.0.0.1:{failPort}",
            ChannelAddress: "/down",
            OperationAction: "send",
            BindingFields: new Dictionary<string, string>());

        var result = await resolver.InvokeAsync(
            context,
            jsonMessages: ["{}"],
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        await serverTask.WaitAsync(TestContext.Current.CancellationToken);
        failListener.Stop();

        Assert.Equal("HTTP 503", result.Status);
        Assert.Equal("503", result.Metadata["http.status"]);
    }

    private static int FindFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
