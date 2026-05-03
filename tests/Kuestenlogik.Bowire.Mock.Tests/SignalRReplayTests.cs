// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// SignalR replay (Phase 2h): the mock speaks the JSON hub protocol —
/// handshake + U+001E-terminated frames + Invocation/StreamItem/
/// Completion/Ping message types — and replays recorded server-side
/// frames, rewriting invocationId for client-initiated calls and
/// keeping server-initiated broadcasts as-is.
/// </summary>
public sealed class SignalRReplayTests : IDisposable
{
    private const char Sep = ''; // SignalR record separator
    private readonly string _tempDir;

    public SignalRReplayTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-signalr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Handshake_Exchange_AckIsEmptyObject()
    {
        var recording = MakeRecording(receivedMessages: []);
        var path = await WriteAsync(recording, "rec-handshake.json");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/hub"), TestContext.Current.CancellationToken);

        // Send the handshake frame.
        await SendFrameAsync(client, """{"protocol":"json","version":1}""");

        // Server acks with {} + record separator.
        var first = await ReceiveFrameAsync(client, TimeSpan.FromSeconds(5));
        Assert.Equal("{}", first);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ServerBroadcast_EmittedAfterHandshake_WithFrameSeparator()
    {
        var serverFrames = new[]
        {
            Frame(0, 0, """{"type":1,"target":"OnMessage","arguments":["hello"]}"""),
            Frame(1, 5, """{"type":1,"target":"OnMessage","arguments":["world"]}""")
        };
        var recording = MakeRecording(receivedMessages: serverFrames);
        var path = await WriteAsync(recording, "rec-broadcast.json");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/hub"), TestContext.Current.CancellationToken);
        await SendFrameAsync(client, """{"protocol":"json","version":1}""");
        _ = await ReceiveFrameAsync(client, TimeSpan.FromSeconds(5)); // {} ack

        var f1 = await ReceiveFrameAsync(client, TimeSpan.FromSeconds(5));
        var f2 = await ReceiveFrameAsync(client, TimeSpan.FromSeconds(5));

        using var d1 = JsonDocument.Parse(f1);
        using var d2 = JsonDocument.Parse(f2);
        Assert.Equal(1, d1.RootElement.GetProperty("type").GetInt32());
        Assert.Equal("OnMessage", d1.RootElement.GetProperty("target").GetString());
        Assert.Equal("hello", d1.RootElement.GetProperty("arguments")[0].GetString());
        Assert.Equal("world", d2.RootElement.GetProperty("arguments")[0].GetString());

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ClientPing_RespondedByServerPing()
    {
        var recording = MakeRecording(receivedMessages: []);
        var path = await WriteAsync(recording, "rec-ping.json");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/hub"), TestContext.Current.CancellationToken);
        await SendFrameAsync(client, """{"protocol":"json","version":1}""");
        _ = await ReceiveFrameAsync(client, TimeSpan.FromSeconds(5)); // ack

        await SendFrameAsync(client, """{"type":6}""");
        var pong = await ReceiveFrameAsync(client, TimeSpan.FromSeconds(5));

        using var doc = JsonDocument.Parse(pong);
        Assert.Equal(6, doc.RootElement.GetProperty("type").GetInt32());

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ClientInvocation_PairedResponse_IdRewrittenToClientChosenId()
    {
        // Recorded: client invoked GetFoo with id "3", server completed
        // with id "3" + result. At replay time the fresh client picks
        // its own id; mock rewrites.
        var serverFrames = new[]
        {
            Frame(0, 0, """{"type":3,"invocationId":"3","result":{"answer":42}}""")
        };
        var clientFrames = new[]
        {
            Frame(0, 0, """{"type":1,"invocationId":"3","target":"GetFoo","arguments":[]}""")
        };
        var recording = MakeRecording(receivedMessages: serverFrames, sentMessages: clientFrames);
        var path = await WriteAsync(recording, "rec-invoke.json");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/hub"), TestContext.Current.CancellationToken);
        await SendFrameAsync(client, """{"protocol":"json","version":1}""");
        _ = await ReceiveFrameAsync(client, TimeSpan.FromSeconds(5)); // ack

        // Drain the recorded server-side broadcast (the Completion)
        // that the emit loop pushes out on its own. Then invoke with
        // our fresh client id; the mock's pairing re-emits the same
        // Completion shape with the rewritten id.
        await ReceiveFrameAsync(client, TimeSpan.FromSeconds(5));

        const string clientId = "client-chosen-xyz";
        await SendFrameAsync(client,
            $$"""{"type":1,"invocationId":"{{clientId}}","target":"GetFoo","arguments":[]}""");

        var response = await ReceiveFrameAsync(client, TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(response);
        Assert.Equal(3, doc.RootElement.GetProperty("type").GetInt32());
        Assert.Equal(clientId, doc.RootElement.GetProperty("invocationId").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("result").GetProperty("answer").GetInt32());

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ClientInvocation_UnpairedTarget_SyntheticErrorCompletion()
    {
        var recording = MakeRecording(receivedMessages: []);
        var path = await WriteAsync(recording, "rec-unpaired.json");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/hub"), TestContext.Current.CancellationToken);
        await SendFrameAsync(client, """{"protocol":"json","version":1}""");
        _ = await ReceiveFrameAsync(client, TimeSpan.FromSeconds(5)); // ack

        const string clientId = "lost-call-1";
        await SendFrameAsync(client,
            $$"""{"type":1,"invocationId":"{{clientId}}","target":"NotRecorded","arguments":[]}""");

        var resp = await ReceiveFrameAsync(client, TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(resp);
        Assert.Equal(3, doc.RootElement.GetProperty("type").GetInt32());
        Assert.Equal(clientId, doc.RootElement.GetProperty("invocationId").GetString());
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.Contains("NotRecorded", err.GetString(), StringComparison.Ordinal);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    // ---- helpers ----

    private static object Frame(int index, long ts, string json) =>
        new { index, timestampMs = ts, data = JsonDocument.Parse(json).RootElement };

    private static object MakeRecording(object[] receivedMessages, object[]? sentMessages = null) => new
    {
        id = "rec_signalr",
        name = "signalr",
        recordingFormatVersion = 2,
        steps = new[]
        {
            new
            {
                id = "step_hub",
                protocol = "signalr",
                service = "Hub",
                method = "/hub",
                methodType = "Duplex",
                httpPath = "/hub",
                httpVerb = "GET",
                status = "OK",
                response = (string?)null,
                receivedMessages,
                sentMessages = sentMessages ?? []
            }
        }
    };

    private async Task<string> WriteAsync(object recording, string fileName)
    {
        var filePath = Path.Combine(_tempDir, fileName);
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);
        return filePath;
    }

    private static async Task SendFrameAsync(ClientWebSocket ws, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json + Sep);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text,
            endOfMessage: true, TestContext.Current.CancellationToken);
    }

    private static async Task<string> ReceiveFrameAsync(ClientWebSocket ws, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Socket closed while waiting for frame");
            await ms.WriteAsync(buffer.AsMemory(0, result.Count), cts.Token);
        } while (!result.EndOfMessage);

        var raw = Encoding.UTF8.GetString(ms.ToArray());
        // Strip the trailing record separator so assertions see the
        // JSON body alone.
        return raw.TrimEnd(Sep);
    }
}
