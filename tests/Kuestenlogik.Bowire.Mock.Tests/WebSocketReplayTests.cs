// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Phase 2e: a recorded WebSocket duplex step replays as real WebSocket
/// frames when a client opens the upgrade. The mock accepts the upgrade,
/// pushes the captured received-side frames paced by their timestampMs,
/// and closes cleanly when the stream ends.
/// </summary>
public sealed class WebSocketReplayTests : IDisposable
{
    private readonly string _tempDir;

    public WebSocketReplayTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-mock-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TextFrames_AreReplayedInOrder()
    {
        // Three text frames captured by WebSocketBowireChannel: each is a
        // { type, text, bytes } envelope where `text` is the parsed JSON
        // value the channel observed on the wire.
        var recording = new
        {
            id = "rec_ws",
            name = "ws text",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_chat",
                    protocol = "websocket",
                    service = "WebSocket",
                    method = "/ws/chat",
                    methodType = "Duplex",
                    status = "OK",
                    response = (string?)null,
                    httpPath = "/ws/chat",
                    httpVerb = "GET",
                    receivedMessages = new[]
                    {
                        Frame(0, 0, new { type = "text", text = "hello" }),
                        Frame(1, 5, new { type = "text", text = "world" }),
                        Frame(2, 10, new { type = "text", text = "bye" })
                    }
                }
            }
        };

        var path = Path.Combine(_tempDir, "rec.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws/chat"), TestContext.Current.CancellationToken);

        var received = await DrainTextFramesAsync(client, expectedCount: 3, TimeSpan.FromSeconds(5));

        Assert.Equal(3, received.Count);
        Assert.Equal("hello", received[0]);
        Assert.Equal("world", received[1]);
        Assert.Equal("bye", received[2]);
    }

    [Fact]
    public async Task BinaryFrame_IsReplayedAsBinaryType()
    {
        var payloadBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var base64 = Convert.ToBase64String(payloadBytes);

        var recording = new
        {
            id = "rec_ws_bin",
            name = "ws binary",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_bin",
                    protocol = "websocket",
                    service = "WebSocket",
                    method = "/ws/bin",
                    methodType = "Duplex",
                    status = "OK",
                    response = (string?)null,
                    httpPath = "/ws/bin",
                    httpVerb = "GET",
                    receivedMessages = new[]
                    {
                        Frame(0, 0, new { type = "binary", bytes = payloadBytes.Length, base64 })
                    }
                }
            }
        };

        var path = Path.Combine(_tempDir, "bin.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws/bin"), TestContext.Current.CancellationToken);

        var buffer = new byte[64];
        var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);

        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.Equal(payloadBytes.Length, result.Count);
        Assert.Equal(payloadBytes, buffer[..result.Count]);
    }

    [Fact]
    public async Task RecordedSubProtocol_IsEchoedOnHandshake_WhenClientRequestsIt()
    {
        // Recorder captures the negotiated sub-protocol into metadata
        // under `_subprotocol`. When the replay client asks for the
        // same sub-protocol in its upgrade request, the mock echoes
        // it back so ClientWebSocket.SubProtocol matches what the
        // original backend negotiated.
        var recording = new
        {
            id = "rec_ws_sub",
            name = "ws subprotocol",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_sub",
                    protocol = "websocket",
                    service = "WebSocket",
                    method = "/ws/sub",
                    methodType = "Duplex",
                    status = "OK",
                    response = (string?)null,
                    httpPath = "/ws/sub",
                    httpVerb = "GET",
                    metadata = new Dictionary<string, string> { ["_subprotocol"] = "wamp.2.json" },
                    receivedMessages = new[]
                    {
                        Frame(0, 0, new { type = "text", text = "ok" })
                    }
                }
            }
        };

        var path = Path.Combine(_tempDir, "sub.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol("wamp.2.json");
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws/sub"), TestContext.Current.CancellationToken);

        Assert.Equal("wamp.2.json", client.SubProtocol);
    }

    [Fact]
    public async Task RecordedSubProtocol_NotRequested_HandshakeStillSucceedsWithoutEcho()
    {
        // Client didn't list the recorded sub-protocol in its Sec-
        // WebSocket-Protocol header, so forcing the echo would fail the
        // handshake with a negotiation mismatch. The mock drops back to
        // a vanilla accept, leaving SubProtocol empty — the replay
        // carries on without the hand-shake drama.
        var recording = new
        {
            id = "rec_ws_sub_unreq",
            name = "ws subprotocol unreq",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_unreq",
                    protocol = "websocket",
                    service = "WebSocket",
                    method = "/ws/unreq",
                    methodType = "Duplex",
                    status = "OK",
                    response = (string?)null,
                    httpPath = "/ws/unreq",
                    httpVerb = "GET",
                    metadata = new Dictionary<string, string> { ["_subprotocol"] = "wamp.2.json" },
                    receivedMessages = new[]
                    {
                        Frame(0, 0, new { type = "text", text = "plain" })
                    }
                }
            }
        };

        var path = Path.Combine(_tempDir, "unreq.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        // No AddSubProtocol — upgrade is vanilla.
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws/unreq"), TestContext.Current.CancellationToken);

        Assert.True(string.IsNullOrEmpty(client.SubProtocol));
        var received = await DrainTextFramesAsync(client, expectedCount: 1, TimeSpan.FromSeconds(5));
        Assert.Single(received);
        Assert.Equal("plain", received[0]);
    }

    [Fact]
    public async Task InputGating_EmitsRecvFramesUpToSendCheckpoint_ThenWaits()
    {
        // Timeline: recv@0 ("greet") → send@10 (client ping) → recv@20
        // ("pong"). Without a client frame the mock must stay at the
        // send-gate and the client must receive only "greet" first.
        var recording = new
        {
            id = "rec_ws_gate",
            name = "ws input gating",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_gate",
                    protocol = "websocket",
                    service = "WebSocket",
                    method = "/ws/gate",
                    methodType = "Duplex",
                    status = "OK",
                    response = (string?)null,
                    httpPath = "/ws/gate",
                    httpVerb = "GET",
                    receivedMessages = new[]
                    {
                        Frame(0, 0, new { type = "text", text = "greet" }),
                        Frame(1, 20, new { type = "text", text = "pong" })
                    },
                    sentMessages = new[]
                    {
                        Frame(0, 10, new { type = "text", text = "ping" })
                    }
                }
            }
        };

        var path = Path.Combine(_tempDir, "gate.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws/gate"), TestContext.Current.CancellationToken);

        // First frame must arrive before the gate.
        var first = await ReadOneTextFrameAsync(client, TimeSpan.FromSeconds(5));
        Assert.Equal("greet", first);

        // Before sending anything, assert no second frame shows up —
        // use a pending ReceiveAsync (NOT cancelled, which would abort
        // the socket) and race it against a short delay. If the delay
        // wins, the gate held. The receive task stays pending so we
        // can await it after we send.
        var buffer = new byte[64];
        var receiveTask = client.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
        var winner = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken));
        Assert.NotSame(receiveTask, winner);

        // Send the ping — gate releases, the pending receive resolves.
        await SendTextAsync(client, "ping");

        var result = await receiveTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(result.EndOfMessage, "second frame arrived fragmented — unexpected for a short text payload");
        Assert.Equal("pong", Encoding.UTF8.GetString(buffer, 0, result.Count));
    }

    [Fact]
    public async Task InputGating_NoSentMessages_ReplaysWithoutWaiting()
    {
        // Backwards-compat: without sentMessages the replayer uses the
        // pre-gating path and emits the whole sequence immediately, so
        // a silent client still gets every frame.
        var recording = new
        {
            id = "rec_ws_nogate",
            name = "ws no gate",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_nogate",
                    protocol = "websocket",
                    service = "WebSocket",
                    method = "/ws/nogate",
                    methodType = "Duplex",
                    status = "OK",
                    response = (string?)null,
                    httpPath = "/ws/nogate",
                    httpVerb = "GET",
                    receivedMessages = new[]
                    {
                        Frame(0, 0, new { type = "text", text = "one" }),
                        Frame(1, 1, new { type = "text", text = "two" })
                    }
                }
            }
        };

        var path = Path.Combine(_tempDir, "nogate.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws/nogate"), TestContext.Current.CancellationToken);

        var received = await DrainTextFramesAsync(client, expectedCount: 2, TimeSpan.FromSeconds(5));
        Assert.Equal(2, received.Count);
        Assert.Equal("one", received[0]);
        Assert.Equal("two", received[1]);
    }

    [Fact]
    public async Task InputGating_MultipleCheckpoints_ReleaseIndependently()
    {
        // Two gates. Client must send once to unlock the middle recv,
        // once more to unlock the final recv. Tests that ticket
        // accounting is per-frame, not "one send releases everything".
        var recording = new
        {
            id = "rec_ws_multi",
            name = "ws multi gate",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_multi",
                    protocol = "websocket",
                    service = "WebSocket",
                    method = "/ws/multi",
                    methodType = "Duplex",
                    status = "OK",
                    response = (string?)null,
                    httpPath = "/ws/multi",
                    httpVerb = "GET",
                    receivedMessages = new[]
                    {
                        Frame(0, 0, new { type = "text", text = "a" }),
                        Frame(1, 20, new { type = "text", text = "b" }),
                        Frame(2, 40, new { type = "text", text = "c" })
                    },
                    sentMessages = new[]
                    {
                        Frame(0, 10, new { type = "text", text = "s1" }),
                        Frame(1, 30, new { type = "text", text = "s2" })
                    }
                }
            }
        };

        var path = Path.Combine(_tempDir, "multi.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws/multi"), TestContext.Current.CancellationToken);

        // Before either send, only "a" arrives.
        Assert.Equal("a", await ReadOneTextFrameAsync(client, TimeSpan.FromSeconds(5)));

        await SendTextAsync(client, "s1");
        Assert.Equal("b", await ReadOneTextFrameAsync(client, TimeSpan.FromSeconds(5)));

        await SendTextAsync(client, "s2");
        Assert.Equal("c", await ReadOneTextFrameAsync(client, TimeSpan.FromSeconds(5)));
    }

    // ---- helpers ----

    private static object Frame(int index, long ts, object data) =>
        new { index, timestampMs = ts, data };

    private static async Task SendTextAsync(ClientWebSocket client, string payload)
    {
        await client.SendAsync(
            Encoding.UTF8.GetBytes(payload),
            WebSocketMessageType.Text,
            endOfMessage: true,
            TestContext.Current.CancellationToken);
    }

    private static async Task<string> ReadOneTextFrameAsync(ClientWebSocket client, TimeSpan timeout)
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

    private static async Task<List<string>> DrainTextFramesAsync(
        ClientWebSocket client, int expectedCount, TimeSpan timeout)
    {
        var received = new List<string>();
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[4096];

        while (received.Count < expectedCount &&
               client.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            WebSocketReceiveResult result;
            try { result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token); }
            catch (OperationCanceledException) { break; }

            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.Count == 0) continue;
            received.Add(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }

        return received;
    }
}
