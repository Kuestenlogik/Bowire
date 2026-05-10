// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.SocketIo;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Drives <see cref="BowireSocketIoProtocol"/>'s DiscoverAsync / InvokeAsync /
/// InvokeStreamAsync paths against a self-hosted, polling-only Socket.IO v4
/// server built directly on <see cref="HttpListener"/>. SocketIOClient 4.0.3
/// defaults to <c>Transport=Polling</c> with <c>AutoUpgrade=true</c>;
/// advertising an empty <c>upgrades</c> array in the engine.io OPEN packet
/// pins the client to polling, sufficient to exercise the plugin's connect /
/// emit / receive surface without bringing in a Socket.IO server library or
/// adding new NuGet references.
/// </summary>
public sealed class BowireSocketIoProtocolIntegrationTests : IAsyncDisposable
{
    private MiniSocketIoServer? _server;

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DiscoverAsync_HappyPath_ReturnsServiceWithDetectedEvents()
    {
        var ct = TestContext.Current.CancellationToken;
        _server = MiniSocketIoServer.Start();
        _server.QueueServerEvent("tick", JsonDocument.Parse("\"first\"").RootElement);
        _server.QueueServerEvent("status", JsonDocument.Parse("{\"ok\":true}").RootElement);

        var protocol = new BowireSocketIoProtocol();
        var services = await protocol.DiscoverAsync(_server.Url, showInternalServices: false, ct);

        var svc = Assert.Single(services);
        Assert.Equal("Socket.IO", svc.Name);
        Assert.Equal("socketio", svc.Package);
        Assert.Equal("socketio", svc.Source);
        Assert.Equal(_server.Url, svc.OriginUrl);
        Assert.Contains(svc.Methods, m => m.Name == "emit" && m.MethodType == "Unary");
        Assert.Contains(svc.Methods, m => m.Name == "listen" && m.MethodType == "ServerStreaming");
        Assert.Contains(svc.Methods, m => m.Name == "tick" && m.MethodType == "ServerStreaming");
        Assert.Contains(svc.Methods, m => m.Name == "status" && m.MethodType == "ServerStreaming");
    }

    [Fact]
    public async Task DiscoverAsync_ServerThatNeverResponds_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        // Bind a TCP listener that accepts connections but never returns a
        // valid engine.io OPEN packet, so SocketIOClient times out and the
        // plugin's catch returns [].
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var protocol = new BowireSocketIoProtocol();
        var services = await protocol.DiscoverAsync(
            $"http://127.0.0.1:{port}", showInternalServices: false, ct);

        Assert.Empty(services);
    }

    [Fact]
    public async Task InvokeAsync_EmitAndReceive_RoundtripsAsResponseEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        _server = MiniSocketIoServer.Start();
        // Echo each incoming client emit straight back as an "ack" event.
        _server.OnClientEvent = (name, payload) =>
        {
            _server.QueueServerEvent("ack",
                JsonSerializer.SerializeToElement(new { echo = name, payload }));
        };

        var protocol = new BowireSocketIoProtocol();
        var result = await protocol.InvokeAsync(
            _server.Url,
            service: "Socket.IO", method: "socketio/emit",
            jsonMessages: ["{\"event\":\"ping\",\"data\":\"hello\"}"],
            showInternalServices: false, metadata: null, ct: ct);

        Assert.Equal("OK", result.Status);
        Assert.Equal("ping", result.Metadata["event"]);
        Assert.NotNull(result.Response);
        using var doc = JsonDocument.Parse(result.Response);
        Assert.Equal("ack", doc.RootElement.GetProperty("event").GetString());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("ping", data.GetProperty("echo").GetString());
    }

    [Fact]
    public async Task InvokeAsync_NoServerResponse_FallsBackToEmittedStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        _server = MiniSocketIoServer.Start();
        // Server is silent on this emit, so the plugin's 3s wait elapses
        // and the result reports the fallback "emitted" envelope rather
        // than a server response.

        var protocol = new BowireSocketIoProtocol();
        var result = await protocol.InvokeAsync(
            _server.Url,
            service: "Socket.IO", method: "socketio/emit",
            jsonMessages: ["{\"event\":\"silent\",\"data\":\"x\"}"],
            showInternalServices: false, metadata: null, ct: ct);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        using var doc = JsonDocument.Parse(result.Response);
        Assert.Equal("silent", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("emitted", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task InvokeAsync_MalformedPayload_DefaultsToMessageEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        _server = MiniSocketIoServer.Start();

        // Malformed JSON: ParseEmitPayload falls through to ("message", raw).
        var protocol = new BowireSocketIoProtocol();
        var result = await protocol.InvokeAsync(
            _server.Url,
            service: "Socket.IO", method: "socketio/emit",
            jsonMessages: ["not-json-at-all"],
            showInternalServices: false, metadata: null, ct: ct);

        Assert.Equal("OK", result.Status);
        Assert.Equal("message", result.Metadata["event"]);
    }

    [Fact]
    public async Task InvokeStreamAsync_ListenForAllEvents_YieldsEverything()
    {
        var ct = TestContext.Current.CancellationToken;
        _server = MiniSocketIoServer.Start();

        var protocol = new BowireSocketIoProtocol();
        using var perTest = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perTest.CancelAfter(TimeSpan.FromSeconds(20));

        // Push two events after the consumer has had a moment to subscribe.
        _ = Task.Run(async () =>
        {
            await Task.Delay(500, perTest.Token);
            _server.QueueServerEvent("alpha", JsonDocument.Parse("\"one\"").RootElement);
            _server.QueueServerEvent("beta", JsonDocument.Parse("\"two\"").RootElement);
        }, perTest.Token);

        var received = new List<string>();
        await foreach (var msg in protocol.InvokeStreamAsync(
                           _server.Url,
                           service: "Socket.IO", method: "listen",
                           jsonMessages: ["{}"],
                           showInternalServices: false, metadata: null,
                           ct: perTest.Token))
        {
            received.Add(msg);
            if (received.Count == 2)
            {
                await perTest.CancelAsync();
                break;
            }
        }

        Assert.Equal(2, received.Count);
        var names = received.Select(m =>
        {
            using var d = JsonDocument.Parse(m);
            return d.RootElement.GetProperty("event").GetString();
        }).ToList();
        Assert.Contains("alpha", names);
        Assert.Contains("beta", names);
    }

    [Fact]
    public async Task InvokeStreamAsync_PerEventMethod_FiltersToSingleEventName()
    {
        var ct = TestContext.Current.CancellationToken;
        _server = MiniSocketIoServer.Start();

        var protocol = new BowireSocketIoProtocol();
        using var perTest = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perTest.CancelAfter(TimeSpan.FromSeconds(20));

        _ = Task.Run(async () =>
        {
            await Task.Delay(500, perTest.Token);
            _server.QueueServerEvent("ping", JsonDocument.Parse("1").RootElement);
            _server.QueueServerEvent("pong", JsonDocument.Parse("2").RootElement);
            _server.QueueServerEvent("ping", JsonDocument.Parse("3").RootElement);
        }, perTest.Token);

        var pings = 0;
        var sawNonPing = false;
        await foreach (var msg in protocol.InvokeStreamAsync(
                           _server.Url,
                           service: "Socket.IO", method: "ping",
                           jsonMessages: [],
                           showInternalServices: false, metadata: null,
                           ct: perTest.Token))
        {
            using var doc = JsonDocument.Parse(msg);
            var name = doc.RootElement.GetProperty("event").GetString();
            if (name == "ping") pings++;
            else sawNonPing = true;
            if (pings == 2)
            {
                await perTest.CancelAsync();
                break;
            }
        }

        Assert.Equal(2, pings);
        Assert.False(sawNonPing, "Per-event method must filter out non-matching events.");
    }

    [Fact]
    public async Task InvokeStreamAsync_EventFilterFromBody_OverridesGenericListen()
    {
        var ct = TestContext.Current.CancellationToken;
        _server = MiniSocketIoServer.Start();

        var protocol = new BowireSocketIoProtocol();
        using var perTest = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perTest.CancelAfter(TimeSpan.FromSeconds(20));

        _ = Task.Run(async () =>
        {
            await Task.Delay(500, perTest.Token);
            _server.QueueServerEvent("other", JsonDocument.Parse("\"skip\"").RootElement);
            _server.QueueServerEvent("targeted", JsonDocument.Parse("\"keep\"").RootElement);
        }, perTest.Token);

        string? receivedEvent = null;
        await foreach (var msg in protocol.InvokeStreamAsync(
                           _server.Url,
                           service: "Socket.IO", method: "listen",
                           jsonMessages: ["{\"event\":\"targeted\"}"],
                           showInternalServices: false, metadata: null,
                           ct: perTest.Token))
        {
            using var doc = JsonDocument.Parse(msg);
            receivedEvent = doc.RootElement.GetProperty("event").GetString();
            await perTest.CancelAsync();
            break;
        }

        Assert.Equal("targeted", receivedEvent);
    }

    [Fact]
    public async Task InvokeStreamAsync_CancellationToken_TerminatesStreamCleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        _server = MiniSocketIoServer.Start();

        var protocol = new BowireSocketIoProtocol();
        using var perTest = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var iterationTask = Task.Run(async () =>
        {
            var count = 0;
            try
            {
                await foreach (var _ in protocol.InvokeStreamAsync(
                                   _server.Url,
                                   service: "Socket.IO", method: "listen",
                                   jsonMessages: [],
                                   showInternalServices: false, metadata: null,
                                   ct: perTest.Token))
                {
                    count++;
                }
            }
            catch (OperationCanceledException) { /* expected */ }
            return count;
        }, ct);

        // Give the client a moment to connect + subscribe before pulling the
        // plug, then assert the consumer task terminates without hanging.
        await Task.Delay(750, ct);
        await perTest.CancelAsync();
        var iterated = await iterationTask.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(0, iterated);
    }

    // ---- Mini Socket.IO v4 polling-only server (HttpListener-backed) ----
    //
    // Just enough engine.io / Socket.IO v4 to satisfy SocketIOClient 4.0.3:
    //   - GET /socket.io/?EIO=4&transport=polling (no sid) -> OPEN packet
    //     with upgrades=[] so the client never tries to switch to WS.
    //   - POST .../?sid=...&transport=polling with body "40" (CONNECT) /
    //     "42[...]" (EVENT) -> 200 OK; events fire OnClientEvent.
    //   - GET .../?sid=...&transport=polling (long-poll) -> return queued
    //     server-to-client packets joined by \x1e, or 200 with "6" (NOOP)
    //     when the long-poll window elapses.
    //
    // Built on HttpListener so the test project doesn't need ASP.NET Core
    // framework references just for the integration tests.
    private sealed class MiniSocketIoServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly ConcurrentDictionary<string, ClientSession> _sessions = new();
        private readonly ConcurrentQueue<string> _pendingBroadcasts = new();
        public string Url { get; }
        public Action<string, JsonElement?>? OnClientEvent { get; set; }

        private MiniSocketIoServer(HttpListener listener, int port)
        {
            _listener = listener;
            Url = $"http://127.0.0.1:{port}";
            _loop = Task.Run(AcceptLoopAsync);
        }

        public static MiniSocketIoServer Start()
        {
            // HttpListener doesn't take port-0 directly. Reserve a free
            // ephemeral port by binding a throwaway TCP listener, capturing
            // the kernel-assigned port, and reusing it for the HttpListener
            // prefix. Loopback only, so racing another binder on the same
            // port is exceptionally unlikely in CI.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                int port;
                using (var tcp = new TcpListener(IPAddress.Loopback, 0))
                {
                    tcp.Start();
                    port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                    tcp.Stop();
                }

                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    return new MiniSocketIoServer(listener, port);
                }
                catch (HttpListenerException) { /* retry on the next free port */ }
            }
            throw new InvalidOperationException("Could not bind a free loopback port.");
        }

        public void QueueServerEvent(string name, JsonElement data)
        {
            var payload = "42" + JsonSerializer.Serialize(new object?[]
            {
                name,
                data.ValueKind == JsonValueKind.Undefined ? null : data.Clone()
            });
            // Push to every live session, and stash a copy for sessions
            // that haven't connected yet so test-setup-before-connect
            // queues still surface to the client once it polls.
            if (_sessions.IsEmpty) _pendingBroadcasts.Enqueue(payload);
            foreach (var session in _sessions.Values)
                session.OutboundQueue.Enqueue(payload);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { _listener.Stop(); } catch { /* best-effort */ }
            try { _listener.Close(); } catch { /* best-effort */ }
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* shutdown */ }
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                if (!ctx.Request.Url!.AbsolutePath.StartsWith("/socket.io", StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }

                var query = ParseQuery(ctx.Request.Url!.Query);
                query.TryGetValue("sid", out var sid);
                query.TryGetValue("transport", out var transport);
                sid ??= "";
                transport ??= "";

                // Reject WebSocket upgrade attempts so the client stays on
                // polling. Empty upgrades[] in the OPEN packet plus a 400
                // here guards against any retry path.
                if (transport == "websocket")
                {
                    ctx.Response.StatusCode = 400;
                    return;
                }

                if (string.IsNullOrEmpty(sid) && ctx.Request.HttpMethod == "GET")
                {
                    await HandleHandshakeAsync(ctx);
                    return;
                }

                if (!_sessions.TryGetValue(sid, out var session))
                {
                    ctx.Response.StatusCode = 400;
                    return;
                }

                if (ctx.Request.HttpMethod == "GET") await HandlePollAsync(ctx, session);
                else if (ctx.Request.HttpMethod == "POST") await HandlePostAsync(ctx, session);
                else ctx.Response.StatusCode = 405;
            }
            catch { /* connection died: nothing to do */ }
            finally { try { ctx.Response.OutputStream.Close(); } catch { } }
        }

        private async Task HandleHandshakeAsync(HttpListenerContext ctx)
        {
            var sid = Guid.NewGuid().ToString("N");
            var session = new ClientSession();
            _sessions[sid] = session;

            // Pre-queue the Socket.IO CONNECT ack so the very first poll
            // after the client's POST 40 surfaces it without an extra
            // round-trip. The plugin sets a 10s ConnectionTimeout, so any
            // dragging here costs us real wall-clock time.
            session.OutboundQueue.Enqueue("40{\"sid\":\"" + sid + "\"}");

            // Drain anything the test queued before the client connected
            // so events arrive on the first post-CONNECT poll.
            while (_pendingBroadcasts.TryDequeue(out var pending))
                session.OutboundQueue.Enqueue(pending);

            var openPacket = "0" + JsonSerializer.Serialize(new
            {
                sid,
                upgrades = Array.Empty<string>(),
                pingInterval = 25000,
                pingTimeout = 60000,
                maxPayload = 1_000_000
            });

            await WriteTextAsync(ctx, openPacket);
        }

        private static async Task HandlePollAsync(HttpListenerContext ctx, ClientSession session)
        {
            // Long-poll: wait up to ~3s for a packet. Tests inject events
            // through QueueServerEvent so a tight loop here is fine.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                if (session.OutboundQueue.TryDequeue(out var first))
                {
                    var packets = new List<string> { first };
                    // Drain back-to-back queued packets so they ride a
                    // single poll response (engine.io v4 joins by \x1e).
                    while (session.OutboundQueue.TryDequeue(out var more))
                        packets.Add(more);
                    await WriteTextAsync(ctx, string.Join('\x1e', packets));
                    return;
                }
                await Task.Delay(25);
            }
            // No traffic this window: NOOP packet ("6") keeps the client
            // polling without it concluding the session has died.
            await WriteTextAsync(ctx, "6");
        }

        private async Task HandlePostAsync(HttpListenerContext ctx, ClientSession session)
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            foreach (var packet in body.Split('\x1e'))
            {
                if (packet.StartsWith("40", StringComparison.Ordinal))
                {
                    // CONNECT: already pre-acked at handshake time.
                    continue;
                }
                if (packet.StartsWith("42", StringComparison.Ordinal))
                {
                    var arrayJson = packet[2..];
                    try
                    {
                        using var doc = JsonDocument.Parse(arrayJson);
                        if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                        var name = doc.RootElement[0].GetString() ?? "";
                        JsonElement? payload = doc.RootElement.GetArrayLength() > 1
                            ? doc.RootElement[1].Clone()
                            : null;
                        OnClientEvent?.Invoke(name, payload);
                    }
                    catch (JsonException) { /* ignore malformed */ }
                }
            }

            await WriteTextAsync(ctx, "ok");
        }

        private static async Task WriteTextAsync(HttpListenerContext ctx, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/plain; charset=UTF-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(query)) return result;
            var trimmed = query.StartsWith('?') ? query[1..] : query;
            foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) result[Uri.UnescapeDataString(pair)] = "";
                else result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            return result;
        }

        private sealed class ClientSession
        {
            public ConcurrentQueue<string> OutboundQueue { get; } = new();
        }
    }
}
