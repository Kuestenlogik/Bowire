// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Gap-filler coverage for <see cref="Kuestenlogik.Bowire.Mock.Replay.UnaryReplayer"/>.
/// The sibling files (UnaryReplayerTests, UnaryReplayerErrorPathTests,
/// WebSocketReplayTests, GrpcReplayTests, SignalRReplayTests,
/// SocketIoReplayTests, SseResumeTests, SseNativeFieldsTests) cover
/// the happy + structured-error paths; this file targets specific
/// remaining branches (no-frames replays, malformed-base64 skip in
/// streaming, SSE field round-trip via FormatSseFrame's BuildSseLines).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861:Prefer static readonly fields over constant array arguments", Justification = "Test scope — array allocations are negligible")]
public sealed class UnaryReplayerGapFillTests : IDisposable
{
    static UnaryReplayerGapFillTests()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    private readonly string _tempDir;

    public UnaryReplayerGapFillTests()
    {
        _tempDir = SafePath.Combine(Path.GetTempPath(), "bowire-gapfill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ---------------- WebSocket "no frames" ----------------

    [Fact]
    public async Task WebSocket_StepWithoutRecordedFrames_AcceptsAndClosesGracefully()
    {
        // ReceivedMessages omitted entirely → frames==null → the
        // "accept + close gracefully" branch (line 856-868 in the
        // replayer) runs; clients shouldn't hang.
        var recording = new
        {
            id = "rec_empty_ws",
            name = "ws empty",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_empty",
                    protocol = "websocket",
                    service = "WebSocket",
                    method = "/ws/empty",
                    methodType = "Duplex",
                    status = "OK",
                    response = (string?)null,
                    httpPath = "/ws/empty",
                    httpVerb = "GET",
                    receivedMessages = Array.Empty<object>(),
                },
            },
        };
        var path = SafePath.Combine(_tempDir, "empty.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0 },
            TestContext.Current.CancellationToken);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws/empty"), TestContext.Current.CancellationToken);

        // Server immediately closes with NormalClosure. Wait for the
        // close frame so the test doesn't race the cleanup.
        var buffer = new byte[64];
        var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, result.CloseStatus);
    }

    // ---------------- gRPC server-streaming malformed base64 frame ----------------

    [Fact]
    public async Task GrpcServerStreaming_MalformedBase64Frame_SkippedButStreamContinues()
    {
        // Three captured frames; the middle one has invalid base64 in
        // its responseBinary. The replayer should LogWarning + skip it
        // (line 480-484 path) and still emit the other two frames + the
        // grpc-status: 0 trailer at the end.
        var ok1 = Convert.ToBase64String(new StringValue { Value = "first" }.ToByteArray());
        var ok2 = Convert.ToBase64String(new StringValue { Value = "third" }.ToByteArray());
        var recording = new
        {
            id = "rec_grpc_stream",
            name = "grpc stream malformed",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_grpc_stream",
                    protocol = "grpc",
                    service = "Stream.S",
                    method = "M",
                    methodType = "ServerStreaming",
                    status = "OK",
                    receivedMessages = new[]
                    {
                        new { index = 0, timestampMs = (long?)null, responseBinary = ok1 },
                        new { index = 1, timestampMs = (long?)null, responseBinary = "@@@nope" },
                        new { index = 2, timestampMs = (long?)null, responseBinary = ok2 },
                    },
                },
            },
        };
        var path = SafePath.Combine(_tempDir, "grpc-stream.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                Watch = false,
                ReplaySpeed = 0,
                HostingExtensions = new IBowireMockHostingExtension[] { new GrpcMockHostingExtension() },
            },
            TestContext.Current.CancellationToken);

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{server.Port}");

        var method = new Method<StringValue, StringValue>(
            MethodType.ServerStreaming, "Stream.S", "M",
            Marshallers.Create(v => v.ToByteArray(), bytes => StringValue.Parser.ParseFrom(bytes)),
            Marshallers.Create(v => v.ToByteArray(), bytes => StringValue.Parser.ParseFrom(bytes)));

        using var call = channel.CreateCallInvoker()
            .AsyncServerStreamingCall(method, host: null, new CallOptions(deadline: DateTime.UtcNow.AddSeconds(10)), new StringValue { Value = "req" });

        var received = new List<string>();
        while (await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken))
        {
            received.Add(call.ResponseStream.Current.Value);
        }
        Assert.Equal(new[] { "first", "third" }, received);
    }

    [Fact]
    public async Task GrpcServerStreaming_WithFrameTimestamps_PacesEmission()
    {
        // Two frames with timestampMs values — drives the pacing
        // branch (line 460-473) at speed=1.0. Keep the gap tiny so
        // the test stays fast.
        var b1 = Convert.ToBase64String(new StringValue { Value = "alpha" }.ToByteArray());
        var b2 = Convert.ToBase64String(new StringValue { Value = "beta" }.ToByteArray());
        var recording = new
        {
            id = "rec_grpc_paced",
            name = "grpc paced",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_paced",
                    protocol = "grpc",
                    service = "Paced.S",
                    method = "M",
                    methodType = "ServerStreaming",
                    status = "OK",
                    receivedMessages = new[]
                    {
                        new { index = 0, timestampMs = (long?)0,   responseBinary = b1 },
                        new { index = 1, timestampMs = (long?)50,  responseBinary = b2 },
                    },
                },
            },
        };
        var path = SafePath.Combine(_tempDir, "grpc-paced.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        // ReplaySpeed = 1.0 (default) — the 50 ms inter-frame delay is honored.
        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                Watch = false,
                ReplaySpeed = 1.0,
                HostingExtensions = new IBowireMockHostingExtension[] { new GrpcMockHostingExtension() },
            },
            TestContext.Current.CancellationToken);

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{server.Port}");

        var method = new Method<StringValue, StringValue>(
            MethodType.ServerStreaming, "Paced.S", "M",
            Marshallers.Create(v => v.ToByteArray(), bytes => StringValue.Parser.ParseFrom(bytes)),
            Marshallers.Create(v => v.ToByteArray(), bytes => StringValue.Parser.ParseFrom(bytes)));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var call = channel.CreateCallInvoker()
            .AsyncServerStreamingCall(method, host: null, new CallOptions(deadline: DateTime.UtcNow.AddSeconds(10)), new StringValue { Value = "go" });

        var received = new List<string>();
        while (await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken))
        {
            received.Add(call.ResponseStream.Current.Value);
        }
        sw.Stop();

        Assert.Equal(new[] { "alpha", "beta" }, received);
        // Pacing should have introduced ~50ms of inter-frame delay; we
        // don't assert exact timing (test-host jitter), just that the
        // pacing branch ran (total > 25ms is a generous floor).
        Assert.True(sw.ElapsedMilliseconds >= 25, $"expected paced emission, total {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task GrpcClientStreaming_ConsumesRequestsAndEmitsSingleResponse()
    {
        // Drives the ReplayGrpcClientStreamAsync path. The client sends
        // three request frames + half-closes; the mock drains them,
        // logs the count, and emits one recorded response + grpc-status.
        var responseBytes = new StringValue { Value = "aggregated" }.ToByteArray();
        var recording = new
        {
            id = "rec_grpc_cstream",
            name = "grpc client-stream",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_cstream",
                    protocol = "grpc",
                    service = "Aggregator.S",
                    method = "Aggregate",
                    methodType = "ClientStreaming",
                    status = "OK",
                    responseBinary = Convert.ToBase64String(responseBytes),
                },
            },
        };
        var path = SafePath.Combine(_tempDir, "grpc-cstream.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                Watch = false,
                ReplaySpeed = 0,
                HostingExtensions = new IBowireMockHostingExtension[] { new GrpcMockHostingExtension() },
            },
            TestContext.Current.CancellationToken);

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{server.Port}");

        var method = new Method<StringValue, StringValue>(
            MethodType.ClientStreaming, "Aggregator.S", "Aggregate",
            Marshallers.Create(v => v.ToByteArray(), bytes => StringValue.Parser.ParseFrom(bytes)),
            Marshallers.Create(v => v.ToByteArray(), bytes => StringValue.Parser.ParseFrom(bytes)));

        using var call = channel.CreateCallInvoker()
            .AsyncClientStreamingCall(method, host: null,
                new CallOptions(deadline: DateTime.UtcNow.AddSeconds(10),
                                cancellationToken: TestContext.Current.CancellationToken));
        await call.RequestStream.WriteAsync(new StringValue { Value = "one" }, TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new StringValue { Value = "two" }, TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new StringValue { Value = "three" }, TestContext.Current.CancellationToken);
        await call.RequestStream.CompleteAsync();

        var response = await call.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("aggregated", response.Value);
    }

    // ---------------- SSE FormatSseFrame edge cases ----------------

    [Fact]
    public void FormatSseFrame_EnvelopeWithExplicitNullId_FallsBackToIndex()
    {
        // Id explicitly set to JSON null → ReadStringField hits the
        // ValueKind.Null arm (line 353) and returns null → caller uses
        // the fallback index.
        var wire = Kuestenlogik.Bowire.Mock.Replay.UnaryReplayer.FormatSseFrame(
            42, """{"id":null,"data":"hello"}""");
        Assert.Contains("id: 42", wire, StringComparison.Ordinal);
        Assert.Contains("data: hello", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSseFrame_EnvelopeWithNullData_EmitsEmptyDataLine()
    {
        // Data explicitly null → ReadDataField hits the Null arm
        // (line 386) and returns "" → exactly one `data: ` line, empty.
        var wire = Kuestenlogik.Bowire.Mock.Replay.UnaryReplayer.FormatSseFrame(
            1, """{"data":null}""");
        Assert.Contains("data: \n", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSseFrame_EnvelopeWithNumericData_SerializesAsRawJson()
    {
        // Data is a JSON number → ReadDataField hits the default
        // switch arm (line 387) and returns the raw JSON text.
        var wire = Kuestenlogik.Bowire.Mock.Replay.UnaryReplayer.FormatSseFrame(
            2, """{"data":42}""");
        Assert.Contains("data: 42", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSseFrame_EnvelopeWithObjectData_EmitsRawJsonOnOneLine()
    {
        // Object payload — also goes through the default switch arm.
        var wire = Kuestenlogik.Bowire.Mock.Replay.UnaryReplayer.FormatSseFrame(
            3, """{"data":{"a":1}}""");
        Assert.Contains("data: {\"a\":1}", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSseFrame_EnvelopeWithRetryInteger_EmitsRetryLine()
    {
        // Retry: numeric → the TryReadRetry branch fires + "retry: …" line is emitted.
        var wire = Kuestenlogik.Bowire.Mock.Replay.UnaryReplayer.FormatSseFrame(
            0, """{"data":"x","retry":15000}""");
        Assert.Contains("retry: 15000", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSseFrame_MultiLineData_EmitsOneDataLinePerSourceLine()
    {
        // Multi-line `data` → BuildSseLines splits on \n and emits one `data:` per line.
        var wire = Kuestenlogik.Bowire.Mock.Replay.UnaryReplayer.FormatSseFrame(
            0, """{"data":"line1\nline2\nline3"}""");
        Assert.Contains("data: line1\n", wire, StringComparison.Ordinal);
        Assert.Contains("data: line2\n", wire, StringComparison.Ordinal);
        Assert.Contains("data: line3\n", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSseFrame_EnvelopeWithEventField_EmitsEventLine()
    {
        var wire = Kuestenlogik.Bowire.Mock.Replay.UnaryReplayer.FormatSseFrame(
            0, """{"event":"ping","data":"x"}""");
        Assert.Contains("event: ping\n", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSseFrame_EnvelopeWithCustomId_UsesItVerbatim()
    {
        // String id wins over the fallback index.
        var wire = Kuestenlogik.Bowire.Mock.Replay.UnaryReplayer.FormatSseFrame(
            99, """{"id":"custom-id","data":"x"}""");
        Assert.Contains("id: custom-id\n", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("id: 99\n", wire, StringComparison.Ordinal);
    }
}
