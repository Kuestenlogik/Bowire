// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Socket.IO replay speaks the engine.io-on-WebSocket wire protocol:
/// engine.io OPEN → Socket.IO CONNECT / CONNECT-ack → EVENT frames
/// (<c>42["name",payload]</c>). These tests drive the mock with a raw
/// <see cref="ClientWebSocket"/> so they verify the exact wire bytes
/// without pulling in a Socket.IO client library.
/// </summary>
public sealed class SocketIoReplayTests : IDisposable
{
    private readonly string _tempDir;

    public SocketIoReplayTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-mock-socketio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Handshake_SendsEngineIoOpen_ThenSocketIoConnectAck()
    {
        var rec = BuildRecording(events: Array.Empty<object>());
        var path = await WriteRecordingAsync(rec, "handshake.json");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Port}/socket.io/?EIO=4&transport=websocket"),
            TestContext.Current.CancellationToken);

        // Frame 1: engine.io OPEN — "0" + JSON describing the session.
        var open = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));
        Assert.StartsWith("0{", open);
        using (var doc = JsonDocument.Parse(open[1..]))
        {
            Assert.True(doc.RootElement.TryGetProperty("sid", out var sid));
            Assert.Equal(JsonValueKind.String, sid.ValueKind);
            Assert.Equal(25000, doc.RootElement.GetProperty("pingInterval").GetInt32());
        }

        // Client sends Socket.IO CONNECT (default namespace).
        await SendTextFrameAsync(client, "40");

        // Frame 2: CONNECT ack. "40" + JSON with a sid.
        var ack = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));
        Assert.StartsWith("40{", ack);
        using (var doc = JsonDocument.Parse(ack[2..]))
        {
            Assert.True(doc.RootElement.TryGetProperty("sid", out _));
        }
    }

    [Fact]
    public async Task RecordedEvents_AreEmittedAsSocketIoEventFrames()
    {
        // Two events in the capture-envelope shape; the mock should emit
        // them as `42["eventName",payload]` Socket.IO EVENT frames.
        var events = new object[]
        {
            new { @event = "tick", data = 42, timestamp = "2026-04-23T12:00:00Z" },
            new { @event = "chat", data = new { user = "ada", msg = "hi" }, timestamp = "2026-04-23T12:00:01Z" }
        };
        var rec = BuildRecording(events);
        var path = await WriteRecordingAsync(rec, "events.json");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Port}/socket.io/?EIO=4&transport=websocket"),
            TestContext.Current.CancellationToken);

        _ = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5)); // engine.io OPEN
        await SendTextFrameAsync(client, "40");
        _ = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5)); // CONNECT ack

        var tickFrame = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));
        var chatFrame = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));

        Assert.Equal("42[\"tick\",42]", tickFrame);
        Assert.Equal("42[\"chat\",{\"user\":\"ada\",\"msg\":\"hi\"}]", chatFrame);
    }

    [Fact]
    public async Task NamedNamespace_PropagatesThroughAckAndEvents()
    {
        // When the client connects to a non-default namespace (e.g.
        // `/admin`), the ack and every subsequent EVENT frame must
        // carry that namespace so the client library routes them to
        // the right socket instance.
        var rec = BuildRecording(events: new object[]
        {
            new { @event = "alert", data = "boom" }
        });
        var path = await WriteRecordingAsync(rec, "ns.json");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Port}/socket.io/?EIO=4&transport=websocket"),
            TestContext.Current.CancellationToken);

        _ = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));
        await SendTextFrameAsync(client, "40/admin,");
        var ack = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));
        Assert.StartsWith("40/admin,", ack);

        var eventFrame = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));
        Assert.Equal("42/admin,[\"alert\",\"boom\"]", eventFrame);
    }

    [Fact]
    public async Task AckCallback_ClientEventWithAckId_ReceivesRecordedAckResponse()
    {
        // Ack frames opt in via the reserved event marker "__ack__".
        // When the client sends 421["ask",...] the mock pops the first
        // ack response and sends 431[<payload array>] back.
        var rec = BuildRecording(events: new object[]
        {
            new { @event = "__ack__", data = new { reply = "pong" } }
        });
        var path = await WriteRecordingAsync(rec, "ack.json");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Port}/socket.io/?EIO=4&transport=websocket"),
            TestContext.Current.CancellationToken);

        _ = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));
        await SendTextFrameAsync(client, "40");
        _ = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));

        // Ack-response frames are siphoned off the broadcast stream, so
        // no `42[...]` shows up until the client emits.
        await SendTextFrameAsync(client, "421[\"ask\",\"hello?\"]");

        var ack = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));
        Assert.Equal("431[{\"reply\":\"pong\"}]", ack);
    }

    [Fact]
    public async Task AckCallback_OrderBased_MultipleAcksPairedWithMultipleEvents()
    {
        // Two __ack__ frames queued; client sends two events with ack
        // ids. FIFO pairing — first ack response goes to the first
        // client event regardless of the ack id the client used.
        var rec = BuildRecording(events: new object[]
        {
            new { @event = "__ack__", data = "first" },
            new { @event = "__ack__", data = "second" }
        });
        var path = await WriteRecordingAsync(rec, "acks-2.json");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Port}/socket.io/?EIO=4&transport=websocket"),
            TestContext.Current.CancellationToken);

        _ = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));
        await SendTextFrameAsync(client, "40");
        _ = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));

        await SendTextFrameAsync(client, "425[\"one\"]");
        Assert.Equal("435[\"first\"]", await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5)));

        await SendTextFrameAsync(client, "4212[\"two\"]");
        Assert.Equal("4312[\"second\"]", await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task BinaryEvent_EmitsPlaceholderHeaderPlusBinaryFrame()
    {
        // Frame envelope with `binary` opts into the engine.io type-5
        // BINARY_EVENT path. The mock sends the placeholder header
        // `51-["name",{"_placeholder":true,"num":0}]` as a text frame,
        // then the attachment bytes as a raw WebSocket binary frame.
        var payloadBytes = new byte[] { 0xC0, 0xDE, 0xBA, 0xBE };
        var base64 = Convert.ToBase64String(payloadBytes);

        var rec = BuildRecording(events: new object[]
        {
            new { @event = "image", binary = base64 }
        });
        var path = await WriteRecordingAsync(rec, "binary.json");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Port}/socket.io/?EIO=4&transport=websocket"),
            TestContext.Current.CancellationToken);

        _ = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));
        await SendTextFrameAsync(client, "40");
        _ = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));

        // Header frame: text.
        var header = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));
        Assert.Equal("51-[\"image\",{\"_placeholder\":true,\"num\":0}]", header);

        // Attachment: binary WS frame carrying exactly the recorded bytes.
        var buffer = new byte[64];
        var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.Equal(payloadBytes.Length, result.Count);
        Assert.Equal(payloadBytes, buffer[..result.Count]);
    }

    [Fact]
    public async Task Ping_IsAnsweredWithPong()
    {
        // engine.io keep-alive. Client sends "2" (PING), mock replies
        // with "3" (PONG) so long-running replays don't drop.
        var rec = BuildRecording(events: Array.Empty<object>());
        var path = await WriteRecordingAsync(rec, "ping.json");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Port}/socket.io/?EIO=4&transport=websocket"),
            TestContext.Current.CancellationToken);

        _ = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5)); // OPEN
        await SendTextFrameAsync(client, "40");
        _ = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5)); // CONNECT ack

        await SendTextFrameAsync(client, "2");
        var pong = await ReadTextFrameAsync(client, TimeSpan.FromSeconds(5));
        Assert.Equal("3", pong);
    }

    // ---- helpers ----

    private static BowireRecording BuildRecording(object[] events)
    {
        var frames = new List<BowireRecordingFrame>();
        for (var i = 0; i < events.Length; i++)
        {
            var raw = JsonSerializer.Serialize(events[i]);
            using var doc = JsonDocument.Parse(raw);
            frames.Add(new BowireRecordingFrame
            {
                Index = i,
                TimestampMs = i, // near-zero pacing
                Data = doc.RootElement.Clone()
            });
        }

        return new BowireRecording
        {
            Id = "rec_socketio",
            Name = "socket.io replay",
            RecordingFormatVersion = 2,
            Steps =
            {
                new BowireRecordingStep
                {
                    Id = "step_socketio",
                    Protocol = "socketio",
                    Service = "Socket.IO",
                    Method = "listen",
                    MethodType = "ServerStreaming",
                    Status = "OK",
                    ReceivedMessages = frames
                }
            }
        };
    }

    private static async Task<string> WriteRecordingAsync(BowireRecording recording, string name)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "bowire-mock-socketio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, name);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);
        return file;
    }

    private static async Task SendTextFrameAsync(ClientWebSocket client, string text)
    {
        await client.SendAsync(
            Encoding.UTF8.GetBytes(text),
            WebSocketMessageType.Text,
            endOfMessage: true,
            TestContext.Current.CancellationToken);
    }

    private static async Task<string> ReadTextFrameAsync(ClientWebSocket client, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[4096];
        await using var ms = new MemoryStream();
        while (true)
        {
            var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Peer closed before a text frame arrived.");
            await ms.WriteAsync(buffer.AsMemory(0, result.Count), cts.Token);
            if (result.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
