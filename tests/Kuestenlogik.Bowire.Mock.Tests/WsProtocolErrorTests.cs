// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Protocol-error close paths for WebSocket-based replay (graphql-
/// transport-ws + SignalR JSON hub). These branches close the socket
/// with <see cref="WebSocketCloseStatus.ProtocolError"/> instead of
/// proceeding into the recorded send loop, and they sit above the
/// happy-path tests in <c>GraphQlSubscriptionReplayTests</c> /
/// <c>SignalRReplayTests</c>.
/// </summary>
public sealed class WsProtocolErrorTests : IDisposable
{
    private const char SignalRSeparator = ''; // SignalR JSON-hub frame terminator
    private readonly string _tempDir;

    public WsProtocolErrorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-ws-err-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private async Task<string> WriteRecordingAsync(string fileName, object recording)
    {
        var path = Path.Combine(_tempDir, fileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);
        return path;
    }

    private static object GraphQlStep(object[]? frames) => new
    {
        id = "step_gql_err",
        protocol = "graphql",
        service = "GraphQL",
        method = "Subscription.x",
        methodType = "ServerStreaming",
        status = "OK",
        response = (string?)null,
        httpPath = "/graphql",
        httpVerb = "GET",
        receivedMessages = frames
    };

    [Fact]
    public async Task GraphQl_NoRecordedFrames_ServerClosesNormallyAfterAccept()
    {
        // Empty subscription recording → mock accepts the upgrade and
        // closes immediately with NormalClosure. Useful when a recording
        // captured the connect-only phase but missed the actual frames.
        var recording = new
        {
            id = "rec",
            name = "empty",
            recordingFormatVersion = 2,
            steps = new[] { GraphQlStep(frames: Array.Empty<object>()) }
        };
        var path = await WriteRecordingAsync("empty.json", recording);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol("graphql-transport-ws");
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/graphql"), TestContext.Current.CancellationToken);

        // Server's first action with no frames is to send a close.
        var buffer = new byte[4096];
        var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, result.CloseStatus);
    }

    [Fact]
    public async Task GraphQl_BadConnectionInit_ClosesWithProtocolError()
    {
        // Client opens the upgrade and sends garbage instead of
        // {"type":"connection_init"}. The replayer's first ReadJsonEnvelope
        // returns ok=false and the socket is closed with ProtocolError.
        var recording = new
        {
            id = "rec",
            name = "bad-init",
            recordingFormatVersion = 2,
            steps = new[]
            {
                GraphQlStep(frames: new object[]
                {
                    new { index = 0, timestampMs = 0, data = new { type = "next", id = "x", payload = new { } } }
                })
            }
        };
        var path = await WriteRecordingAsync("bad-init.json", recording);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol("graphql-transport-ws");
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/graphql"), TestContext.Current.CancellationToken);

        await client.SendAsync(
            Encoding.UTF8.GetBytes("not-valid-json"),
            WebSocketMessageType.Text,
            endOfMessage: true,
            TestContext.Current.CancellationToken);

        var buffer = new byte[4096];
        var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.ProtocolError, result.CloseStatus);
    }

    [Fact]
    public async Task GraphQl_SubscribeWithoutId_ClosesWithProtocolError()
    {
        // Client sends connection_init then a subscribe missing the `id`
        // field. The replayer returns 101 + ProtocolError close.
        var recording = new
        {
            id = "rec",
            name = "no-sub-id",
            recordingFormatVersion = 2,
            steps = new[]
            {
                GraphQlStep(frames: new object[]
                {
                    new { index = 0, timestampMs = 0, data = new { type = "next", id = "x", payload = new { } } }
                })
            }
        };
        var path = await WriteRecordingAsync("no-sub-id.json", recording);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol("graphql-transport-ws");
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/graphql"), TestContext.Current.CancellationToken);

        await SendTextAsync(client, """{"type":"connection_init"}""");
        // First server reply is connection_ack — drain it.
        var ackBuf = new byte[4096];
        _ = await client.ReceiveAsync(new ArraySegment<byte>(ackBuf), TestContext.Current.CancellationToken);

        // Send subscribe without an id.
        await SendTextAsync(client, """{"type":"subscribe","payload":{"query":"subscription { x }"}}""");

        var buffer = new byte[4096];
        var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.ProtocolError, result.CloseStatus);
    }

    [Fact]
    public async Task SignalR_HandshakeWithUnsupportedProtocol_RepliesErrorAndCloses()
    {
        var recording = SignalRRecording();
        var path = await WriteRecordingAsync("signalr-bad-proto.json", recording);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/hub"), TestContext.Current.CancellationToken);

        // SignalR-style handshake but with an unsupported protocol.
        await SendTextAsync(client, """{"protocol":"messagepack","version":1}""" + SignalRSeparator);

        // First server reply is a JSON error frame, then it closes with ProtocolError.
        var first = await ReceiveTextAsync(client);
        Assert.Contains("error", first, StringComparison.Ordinal);
        Assert.Contains("json", first, StringComparison.OrdinalIgnoreCase);

        var buffer = new byte[1024];
        var closeResult = await client.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketMessageType.Close, closeResult.MessageType);
        Assert.Equal(WebSocketCloseStatus.ProtocolError, closeResult.CloseStatus);
    }

    [Fact]
    public async Task SignalR_HandshakeMissingProtocolField_ClosesWithProtocolError()
    {
        var recording = SignalRRecording();
        var path = await WriteRecordingAsync("signalr-missing-proto.json", recording);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/hub"), TestContext.Current.CancellationToken);

        // Handshake JSON without the required `protocol` field.
        await SendTextAsync(client, """{"version":1}""" + SignalRSeparator);

        var buffer = new byte[1024];
        var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.ProtocolError, result.CloseStatus);
    }

    [Fact]
    public async Task SignalR_MalformedHandshakeJson_ClosesWithProtocolError()
    {
        var recording = SignalRRecording();
        var path = await WriteRecordingAsync("signalr-bad-json.json", recording);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/hub"), TestContext.Current.CancellationToken);

        // Garbage handshake → JsonException → close ProtocolError.
        await SendTextAsync(client, "not-actually-json" + SignalRSeparator);

        var buffer = new byte[1024];
        var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.ProtocolError, result.CloseStatus);
    }

    // Single-step empty recording for the SignalR replay path.
    private static object SignalRRecording() => new
    {
        id = "rec_signalr",
        name = "signalr",
        recordingFormatVersion = 2,
        steps = new[]
        {
            new
            {
                id = "step_signalr",
                protocol = "signalr",
                service = "Hub",
                method = "Hub",
                methodType = "Duplex",
                status = "OK",
                response = (string?)null,
                httpPath = "/hub",
                httpVerb = "GET",
                receivedMessages = Array.Empty<object>()
            }
        }
    };

    private static async Task SendTextAsync(ClientWebSocket client, string text)
    {
        await client.SendAsync(
            Encoding.UTF8.GetBytes(text),
            WebSocketMessageType.Text,
            endOfMessage: true,
            TestContext.Current.CancellationToken);
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket client)
    {
        var buffer = new byte[4096];
        await using var ms = new MemoryStream();
        while (true)
        {
            var r = await client.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("peer closed before text frame");
            await ms.WriteAsync(buffer.AsMemory(0, r.Count), TestContext.Current.CancellationToken);
            if (r.EndOfMessage) break;
        }
        var text = Encoding.UTF8.GetString(ms.ToArray());
        // Drop SignalR's terminator if present.
        return text.TrimEnd(SignalRSeparator);
    }
}
