// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.WebSocket;

namespace Kuestenlogik.Bowire.Protocol.WebSocket.Tests;

/// <summary>
/// Closes the remaining coverage gaps in the WebSocket plugin that the
/// helper- and discovery-focused suites can't reach without an actual
/// peer: the live channel's send-loop / receive-loop / close / dispose,
/// sub-protocol negotiation against a real server, instance properties
/// on an open channel, server-initiated close, and the cross-plugin
/// <c>OpenAsync</c> entry point including its invalid-URL guard.
///
/// The peer is a self-hosted <see cref="HttpListener"/> with WebSocket
/// upgrade support — no Kestrel / ASP.NET dependency on the test
/// project, no network access (loopback only). Agent D's companion
/// suite (<c>WebSocketChannelAndDiscoveryGapsTests</c>) covers the
/// private static helpers + routed-endpoint discovery; this file is
/// strictly about the live-channel surface.
/// </summary>
public sealed class WebSocketAdditionalGapsTests
{
    // ---------- Property surface on a live channel -------------------
    // The getters fire only after CreateAsync returns a wired-up
    // instance — so the only way to hit them is through a real
    // handshake against a peer.

    [Fact]
    public async Task LiveChannel_Properties_Reflect_Open_Connection()
    {
        await using var server = await EchoWebSocketServer.StartAsync();
        await using var channel = await server.OpenChannelAsync(TestContext.Current.CancellationToken);

        // Id is a 32-char hex GUID — stable per channel.
        Assert.Matches("^[a-f0-9]{32}$", channel.Id);
        Assert.True(channel.IsClientStreaming);
        Assert.True(channel.IsServerStreaming);
        Assert.False(channel.IsClosed);
        Assert.Equal(0, channel.SentCount);
        // Stopwatch is started in the ctor; by the time await unwinds
        // it has measured at least one tick of monotonic time. Use
        // >= 0 so a fast CI box doesn't fail spuriously on tick 0.
        Assert.True(channel.ElapsedMs >= 0);
        // No sub-protocol asked for, no sub-protocol negotiated.
        Assert.Null(channel.NegotiatedSubProtocol);
    }

    [Fact]
    public async Task LiveChannel_SendAsync_Increments_SentCount_Per_Call()
    {
        await using var server = await EchoWebSocketServer.StartAsync();
        await using var channel = await server.OpenChannelAsync(TestContext.Current.CancellationToken);

        Assert.True(await channel.SendAsync("first", TestContext.Current.CancellationToken));
        Assert.True(await channel.SendAsync("second", TestContext.Current.CancellationToken));
        Assert.True(await channel.SendAsync("""{"type":"text","text":"third"}""", TestContext.Current.CancellationToken));

        Assert.Equal(3, channel.SentCount);
    }

    // ---------- Receive-loop: text + binary envelope shapes ----------
    // The envelope is { type, text, bytes } for text and
    // { type, binary, bytes, base64 } for binary. We need an actual
    // text frame and an actual binary frame to land for the JSON
    // serialisation branches to execute.

    [Fact]
    public async Task LiveChannel_ReceiveLoop_Surfaces_Text_Frames_As_Json_Envelope()
    {
        await using var server = await EchoWebSocketServer.StartAsync();
        await using var channel = await server.OpenChannelAsync(TestContext.Current.CancellationToken);

        await channel.SendAsync("hello", TestContext.Current.CancellationToken);

        var envelope = await ReadOneAsync(channel, TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(envelope);
        Assert.Equal("text", doc.RootElement.GetProperty("type").GetString());
        // Echo server returns "echo: hello" — server treats it as raw
        // text (not JSON) so the receive-loop embeds it as a string
        // instead of a nested JSON object.
        Assert.Equal("echo: hello", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal(Encoding.UTF8.GetByteCount("echo: hello"), doc.RootElement.GetProperty("bytes").GetInt32());
    }

    [Fact]
    public async Task LiveChannel_ReceiveLoop_Embeds_Valid_Json_Text_As_Parsed_Object()
    {
        // The text branch tries JsonSerializer.Deserialize<JsonElement>
        // first so structured JSON gets rendered as a nested object in
        // the envelope (no backslash-escape soup in the UI). Drive that
        // path by echoing a JSON payload.
        await using var server = await EchoJsonWebSocketServer.StartAsync();
        await using var channel = await server.OpenChannelAsync(TestContext.Current.CancellationToken);

        await channel.SendAsync("""{"hello":"world"}""", TestContext.Current.CancellationToken);

        var envelope = await ReadOneAsync(channel, TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(envelope);
        Assert.Equal("text", doc.RootElement.GetProperty("type").GetString());
        var text = doc.RootElement.GetProperty("text");
        Assert.Equal(JsonValueKind.Object, text.ValueKind);
        Assert.Equal("world", text.GetProperty("hello").GetString());
    }

    [Fact]
    public async Task LiveChannel_ReceiveLoop_Surfaces_Binary_Frames_As_Base64_Envelope()
    {
        await using var server = await EchoWebSocketServer.StartAsync();
        await using var channel = await server.OpenChannelAsync(TestContext.Current.CancellationToken);

        // Bytes 0x01 0x02 0x03 → base64 "AQID"
        await channel.SendAsync("""{"type":"binary","base64":"AQID"}""", TestContext.Current.CancellationToken);

        var envelope = await ReadOneAsync(channel, TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(envelope);
        Assert.Equal("binary", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("bytes").GetInt32());
        Assert.Equal("AQID", doc.RootElement.GetProperty("base64").GetString());
    }

    // ---------- Sub-protocol negotiation -----------------------------
    // The server picks "v2" — the channel must expose it on
    // NegotiatedSubProtocol once the handshake completes.

    [Fact]
    public async Task LiveChannel_SubProtocol_Negotiation_Surfaces_Server_Choice()
    {
        await using var server = await EchoWebSocketServer.StartAsync(serverSubProtocol: "v2");
        await using var channel = await server.OpenChannelAsync(
            TestContext.Current.CancellationToken,
            subProtocols: ["v1", "v2"]);

        // HttpListener echoes back the first sub-protocol it understands;
        // our server is hard-wired to "v2".
        Assert.Equal("v2", channel.NegotiatedSubProtocol);
    }

    // ---------- Server-initiated close → "close" envelope ------------
    // The receive-loop's WebSocketMessageType.Close branch serialises
    // { type, status, description } before the loop exits. The Status
    // numeric value comes from WebSocketCloseStatus.

    [Fact]
    public async Task LiveChannel_Server_Close_Surfaces_Close_Envelope_With_Status_And_Description()
    {
        await using var server = await ServerInitiatedCloseWebSocketServer.StartAsync(
            WebSocketCloseStatus.NormalClosure, "server goodbye");
        await using var channel = await server.OpenChannelAsync(TestContext.Current.CancellationToken);

        var envelope = await ReadOneAsync(channel, TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(envelope);
        Assert.Equal("close", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal((int)WebSocketCloseStatus.NormalClosure, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("server goodbye", doc.RootElement.GetProperty("description").GetString());
    }

    // ---------- CloseAsync ↔ SendAsync idempotency -------------------
    // After CloseAsync flips IsClosed = true, SendAsync must short-
    // circuit to false (the guard) and CloseAsync itself must be a
    // no-op on the second call (no exception). Both branches are
    // currently uncovered.

    [Fact]
    public async Task LiveChannel_CloseAsync_Is_Idempotent_And_SendAsync_Returns_False_After_Close()
    {
        await using var server = await EchoWebSocketServer.StartAsync();
        await using var channel = await server.OpenChannelAsync(TestContext.Current.CancellationToken);

        await channel.CloseAsync(TestContext.Current.CancellationToken);
        Assert.True(channel.IsClosed);

        // Second close is a no-op — does not throw.
        await channel.CloseAsync(TestContext.Current.CancellationToken);

        // Send after close → false (guard fires), SentCount unchanged.
        var sentCountBefore = channel.SentCount;
        var sent = await channel.SendAsync("after-close", TestContext.Current.CancellationToken);
        Assert.False(sent);
        Assert.Equal(sentCountBefore, channel.SentCount);
    }

    // ---------- ReadResponsesAsync cancellation ----------------------
    // The IAsyncEnumerable surface respects the supplied token — when
    // the consumer cancels, MoveNextAsync throws OperationCanceledException
    // (or one of its subtypes; either is acceptable for cancellation).

    [Fact]
    public async Task LiveChannel_ReadResponsesAsync_Propagates_Caller_Cancellation()
    {
        await using var server = await EchoWebSocketServer.StartAsync();
        await using var channel = await server.OpenChannelAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        var enumerator = channel.ReadResponsesAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // Cancel before any frame arrives — channel is empty, the
        // enumerator is parked on ReadAllAsync.
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await enumerator.MoveNextAsync();
        });

        await enumerator.DisposeAsync();
    }

    // ---------- DisposeAsync after a normal lifecycle ----------------
    // DisposeAsync flips IsClosed, cancels the linked CTS, awaits both
    // loops, and disposes the underlying socket. After dispose, every
    // subsequent operation must be safely idempotent.

    [Fact]
    public async Task LiveChannel_DisposeAsync_Marks_Closed_And_SendAsync_Becomes_NoOp()
    {
        var server = await EchoWebSocketServer.StartAsync();
        var channel = await server.OpenChannelAsync(TestContext.Current.CancellationToken);

        await channel.SendAsync("hello", TestContext.Current.CancellationToken);
        await ReadOneAsync(channel, TimeSpan.FromSeconds(5));

        await channel.DisposeAsync();

        Assert.True(channel.IsClosed);
        Assert.False(await channel.SendAsync("post-dispose", TestContext.Current.CancellationToken));

        await server.DisposeAsync();
    }

    // ---------- OpenAsync (IInlineWebSocketChannel cross-plugin) -----

    [Fact]
    public async Task OpenAsync_Invalid_Url_Throws_InvalidOperationException()
    {
        var protocol = new BowireWebSocketProtocol();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await protocol.OpenAsync(
                "not a url",
                subProtocols: null,
                headers: null,
                ct: TestContext.Current.CancellationToken));

        Assert.Contains("Invalid WebSocket URL", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a url", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_Live_Endpoint_Returns_Open_Channel_With_Negotiated_SubProtocol()
    {
        await using var server = await EchoWebSocketServer.StartAsync(serverSubProtocol: "graphql-transport-ws");
        var protocol = new BowireWebSocketProtocol();

        var channel = await protocol.OpenAsync(
            server.Url,
            subProtocols: ["graphql-transport-ws"],
            headers: new Dictionary<string, string> { ["X-Test"] = "yes" },
            ct: TestContext.Current.CancellationToken);

        await using (channel)
        {
            Assert.False(channel.IsClosed);
            // Cast to the WebSocket-channel-specific surface to read the
            // negotiated sub-protocol — the cross-plugin contract is
            // IBowireChannel but the concrete type exposes more.
            var ws = Assert.IsType<WebSocketBowireChannel>(channel, exactMatch: false);
            Assert.Equal("graphql-transport-ws", ws.NegotiatedSubProtocol);
        }
    }

    // ---------- OpenChannelAsync nil paths ---------------------------
    // ResolveUri returns null for empty server + relative method.
    // OpenChannelAsync forwards the null to the caller.

    [Fact]
    public async Task OpenChannelAsync_Empty_ServerUrl_And_Relative_Method_Returns_Null()
    {
        var protocol = new BowireWebSocketProtocol();

        var channel = await protocol.OpenChannelAsync(
            serverUrl: "",
            service: "WebSocket",
            method: "/ws/echo",
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(channel);
    }

    [Fact]
    public async Task OpenChannelAsync_Live_Endpoint_With_SubProtocol_Metadata_Negotiates_It()
    {
        // Drive the full metadata → ExtractSubProtocols → CreateAsync
        // path end-to-end against a live server. The metadata key is
        // stripped before the headers reach the wire (the server fails
        // the handshake if it sees an unknown X-Bowire-* header — we
        // don't assert that here, only that the sub-protocol made it
        // through).
        await using var server = await EchoWebSocketServer.StartAsync(serverSubProtocol: "v2");
        var protocol = new BowireWebSocketProtocol();

        var channel = await protocol.OpenChannelAsync(
            serverUrl: server.Url,
            service: "WebSocket",
            method: server.Path,
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                [BowireWebSocketProtocol.SubProtocolMetadataKey] = "v1, v2",
                ["X-Trace-Id"] = "abc-123"
            },
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(channel);
        await using (channel)
        {
            var ws = Assert.IsType<WebSocketBowireChannel>(channel, exactMatch: false);
            Assert.Equal("v2", ws.NegotiatedSubProtocol);
        }
    }

    // ---------- Description getter (uncovered) -----------------------

    [Fact]
    public void Description_Surfaces_Plugin_Tagline()
    {
        var protocol = new BowireWebSocketProtocol();

        // Pinning the literal — the description is part of the public
        // plugin manifest the sidebar shows. A silent edit here would
        // change what users see.
        Assert.Equal(
            "Raw WebSocket — bidirectional message stream with optional sub-protocol negotiation.",
            protocol.Description);
    }

    // ---------- IsWebSocketScheme: wss branch ------------------------
    // The first OrdinalIgnoreCase comparison matches "ws", short-
    // circuiting before "wss" ever runs in earlier tests. Exercise the
    // wss path explicitly so the half-covered branch closes.

    [Fact]
    public void IsWebSocketScheme_Recognises_Wss_Lowercase()
    {
        var method = typeof(BowireWebSocketProtocol).GetMethod(
            "IsWebSocketScheme", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.True((bool)method.Invoke(null, ["wss"])!);
        Assert.True((bool)method.Invoke(null, ["WSS"])!);
        Assert.False((bool)method.Invoke(null, ["http"])!);
    }

    // =================================================================
    // Helpers
    // =================================================================

    /// <summary>
    /// Pulls exactly one envelope from <paramref name="channel"/> with a
    /// hard timeout so a stuck receive-loop fails the test instead of
    /// hanging the run.
    /// </summary>
    private static async Task<string> ReadOneAsync(IBowireChannel channel, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var envelope in channel.ReadResponsesAsync(cts.Token))
        {
            return envelope;
        }
        throw new InvalidOperationException("Channel completed without producing a frame");
    }

    // -----------------------------------------------------------------
    // Self-hosted WebSocket peer using HttpListener — no Kestrel /
    // AspNetCore reference needed. Loopback only.
    // -----------------------------------------------------------------

    /// <summary>
    /// Echo peer. Text frames come back prefixed with "echo: "; binary
    /// frames are returned verbatim. <c>StartAsync</c>'s
    /// <c>serverSubProtocol</c> argument fixes the negotiated sub-
    /// protocol when the client offers one (or null for none).
    /// </summary>
    private sealed class EchoWebSocketServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string Url { get; }
        public string Path { get; }

        private EchoWebSocketServer(HttpListener listener, string url, string path, string? serverSubProtocol)
        {
            _listener = listener;
            Url = url;
            Path = path;
            _loop = Task.Run(() => RunAsync(serverSubProtocol));
        }

        public static Task<EchoWebSocketServer> StartAsync(string? serverSubProtocol = null)
        {
            var (listener, url, path) = ListenerHelpers.StartLoopback();
            return Task.FromResult(new EchoWebSocketServer(listener, url, path, serverSubProtocol));
        }

        public Task<IBowireChannel> OpenChannelAsync(
            CancellationToken ct, IReadOnlyList<string>? subProtocols = null)
        {
            var protocol = new BowireWebSocketProtocol();
            return OpenChannelOrThrow(protocol, Url, subProtocols, ct);
        }

        private async Task RunAsync(string? serverSubProtocol)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { return; }

                    _ = Task.Run(() => HandleAsync(ctx, serverSubProtocol));
                }
            }
            catch { /* shutting down */ }
        }

        private static async Task HandleAsync(HttpListenerContext ctx, string? serverSubProtocol)
        {
            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }

            HttpListenerWebSocketContext wsCtx;
            try { wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: serverSubProtocol); }
            catch { ctx.Response.Close(); return; }

            var ws = wsCtx.WebSocket;
            var buffer = new byte[8 * 1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    try { result = await ws.ReceiveAsync(buffer, CancellationToken.None); }
                    catch { return; }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        var reply = Encoding.UTF8.GetBytes("echo: " + text);
                        await ws.SendAsync(reply, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    else
                    {
                        await ws.SendAsync(buffer.AsMemory(0, result.Count), WebSocketMessageType.Binary, true, CancellationToken.None);
                    }
                }
            }
            finally
            {
                ws.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { await _loop; } catch { }
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Echoes the raw text frame verbatim (no "echo: " prefix) so the
    /// receive-loop's JSON-parse-success branch fires.
    /// </summary>
    private sealed class EchoJsonWebSocketServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string Url { get; }
        public string Path { get; }

        private EchoJsonWebSocketServer(HttpListener listener, string url, string path)
        {
            _listener = listener;
            Url = url;
            Path = path;
            _loop = Task.Run(RunAsync);
        }

        public static Task<EchoJsonWebSocketServer> StartAsync()
        {
            var (listener, url, path) = ListenerHelpers.StartLoopback();
            return Task.FromResult(new EchoJsonWebSocketServer(listener, url, path));
        }

        public Task<IBowireChannel> OpenChannelAsync(CancellationToken ct)
        {
            var protocol = new BowireWebSocketProtocol();
            return OpenChannelOrThrow(protocol, Url, subProtocols: null, ct);
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { return; }
                    _ = Task.Run(() => HandleAsync(ctx));
                }
            }
            catch { }
        }

        private static async Task HandleAsync(HttpListenerContext ctx)
        {
            if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); return; }
            HttpListenerWebSocketContext wsCtx;
            try { wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null); }
            catch { ctx.Response.Close(); return; }
            var ws = wsCtx.WebSocket;
            var buffer = new byte[8 * 1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    try { result = await ws.ReceiveAsync(buffer, CancellationToken.None); }
                    catch { return; }
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        return;
                    }
                    await ws.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, true, CancellationToken.None);
                }
            }
            finally { ws.Dispose(); }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { await _loop; } catch { }
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Peer that immediately initiates a server-side close with the
    /// configured status + description, so the client's receive-loop
    /// hits the WebSocketMessageType.Close branch.
    /// </summary>
    private sealed class ServerInitiatedCloseWebSocketServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string Url { get; }
        public string Path { get; }

        private ServerInitiatedCloseWebSocketServer(
            HttpListener listener, string url, string path,
            WebSocketCloseStatus status, string description)
        {
            _listener = listener;
            Url = url;
            Path = path;
            _loop = Task.Run(() => RunAsync(status, description));
        }

        public static Task<ServerInitiatedCloseWebSocketServer> StartAsync(
            WebSocketCloseStatus status, string description)
        {
            var (listener, url, path) = ListenerHelpers.StartLoopback();
            return Task.FromResult(new ServerInitiatedCloseWebSocketServer(listener, url, path, status, description));
        }

        public Task<IBowireChannel> OpenChannelAsync(CancellationToken ct)
        {
            var protocol = new BowireWebSocketProtocol();
            return OpenChannelOrThrow(protocol, Url, subProtocols: null, ct);
        }

        private async Task RunAsync(WebSocketCloseStatus status, string description)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { return; }
                    _ = Task.Run(() => HandleAsync(ctx, status, description));
                }
            }
            catch { }
        }

        private static async Task HandleAsync(HttpListenerContext ctx,
            WebSocketCloseStatus status, string description)
        {
            if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); return; }
            HttpListenerWebSocketContext wsCtx;
            try { wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null); }
            catch { ctx.Response.Close(); return; }
            using var ws = wsCtx.WebSocket;
            try
            {
                await ws.CloseAsync(status, description, CancellationToken.None);
            }
            catch { }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { await _loop; } catch { }
            _cts.Dispose();
        }
    }

    private static async Task<IBowireChannel> OpenChannelOrThrow(
        BowireWebSocketProtocol protocol, string baseUrl,
        IReadOnlyList<string>? subProtocols, CancellationToken ct)
    {
        var headers = subProtocols is null || subProtocols.Count == 0
            ? null
            : new Dictionary<string, string>
            {
                [BowireWebSocketProtocol.SubProtocolMetadataKey] = string.Join(',', subProtocols)
            };

        // Hit the public OpenChannelAsync entry point so the metadata
        // → ExtractSubProtocols → CreateAsync wiring is exercised end-
        // to-end, not just the channel constructor.
        var channel = await protocol.OpenChannelAsync(
            serverUrl: baseUrl,
            service: "WebSocket",
            method: "/",
            showInternalServices: false,
            metadata: headers,
            ct: ct);

        return channel ?? throw new InvalidOperationException("OpenChannelAsync returned null");
    }

    private static class ListenerHelpers
    {
        /// <summary>
        /// Allocate a free TCP port (via a transient listener), then
        /// bind an HttpListener prefix on it. Returns the listener,
        /// the ws:// URL and the path the server listens on.
        /// </summary>
        public static (HttpListener Listener, string Url, string Path) StartLoopback()
        {
            // HttpListener requires the prefix to be reserved; loopback
            // works without admin on Windows + Linux. Bind a random
            // free port to avoid cross-test collisions.
            var port = GetFreePort();
            var prefix = $"http://localhost:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            return (listener, $"ws://localhost:{port}/", "/");
        }

        private static int GetFreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
