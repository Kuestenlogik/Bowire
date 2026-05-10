// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Mock.Replay;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Coverage for the structured-error / Not501 fallback paths in
/// <see cref="UnaryReplayer"/> plus the smaller helpers exposed as
/// internals (FormatSseFrame, FormatSocketIoEventFrame,
/// PartitionSocketIoFrames, TryParseSocketIoEventWithAckId,
/// TryExtractSocketIoBinaryFrame, ResolveRecordedSubProtocol).
/// Drives the replayer directly against a <see cref="DefaultHttpContext"/>
/// to keep the suite host-free and OS-agnostic.
/// </summary>
public sealed class UnaryReplayerErrorPathTests
{
    private static (DefaultHttpContext ctx, MemoryStream body) NewCtx()
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream();
        ctx.Response.Body = body;
        return (ctx, body);
    }

    private static string ReadBody(MemoryStream body)
    {
        body.Position = 0;
        return Encoding.UTF8.GetString(body.ToArray());
    }

    [Fact]
    public async Task Replay_UnsupportedProtocolCombination_Returns501WithReRecordHint()
    {
        var (ctx, body) = NewCtx();
        var step = new BowireRecordingStep
        {
            Id = "step_x",
            Protocol = "unknown-proto",
            Service = "S",
            Method = "M",
            MethodType = "Unary",
            Status = "OK"
        };

        var status = await UnaryReplayer.ReplayAsync(
            ctx, step, new MockOptions(), NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(501, status);
        Assert.Equal(501, ctx.Response.StatusCode);
        Assert.Contains("unknown-proto", ReadBody(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrpcUnary_MissingResponseBinary_Returns501()
    {
        var (ctx, body) = NewCtx();
        var step = new BowireRecordingStep
        {
            Id = "step_grpc_no_bin",
            Protocol = "grpc",
            Service = "Svc",
            Method = "M",
            MethodType = "Unary",
            Status = "OK",
            ResponseBinary = null
        };

        var status = await UnaryReplayer.ReplayAsync(
            ctx, step, new MockOptions(), NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(501, status);
        Assert.Contains("responseBinary", ReadBody(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrpcUnary_MalformedBase64_Returns501WithFormatError()
    {
        var (ctx, body) = NewCtx();
        var step = new BowireRecordingStep
        {
            Id = "step_grpc_bad",
            Protocol = "grpc",
            Service = "Svc",
            Method = "M",
            MethodType = "Unary",
            Status = "OK",
            // '@' is not a valid base64 character → FormatException.
            ResponseBinary = "@@@not-base64@@@"
        };

        var status = await UnaryReplayer.ReplayAsync(
            ctx, step, new MockOptions(), NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(501, status);
        Assert.Contains("malformed base64", ReadBody(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrpcServerStreaming_NoReceivedMessages_Returns501()
    {
        var (ctx, body) = NewCtx();
        var step = new BowireRecordingStep
        {
            Id = "step_grpc_stream_empty",
            Protocol = "grpc",
            Service = "Svc",
            Method = "M",
            MethodType = "ServerStreaming",
            Status = "OK"
        };

        var status = await UnaryReplayer.ReplayAsync(
            ctx, step, new MockOptions(), NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(501, status);
        Assert.Contains("receivedMessages", ReadBody(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrpcServerStreaming_FrameWithoutResponseBinary_Returns501()
    {
        var (ctx, body) = NewCtx();
        var step = new BowireRecordingStep
        {
            Id = "step_grpc_stream_partial",
            Protocol = "grpc",
            Service = "Svc",
            Method = "M",
            MethodType = "ServerStreaming",
            Status = "OK",
            ReceivedMessages = new List<BowireRecordingFrame>
            {
                new() { Index = 0, TimestampMs = 0, ResponseBinary = "AAAA" },
                new() { Index = 1, TimestampMs = 5, ResponseBinary = null }
            }
        };

        var status = await UnaryReplayer.ReplayAsync(
            ctx, step, new MockOptions(), NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(501, status);
        Assert.Contains("responseBinary", ReadBody(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrpcClientStreaming_MissingResponseBinary_Returns501()
    {
        var (ctx, body) = NewCtx();
        var step = new BowireRecordingStep
        {
            Id = "step_cs_no_bin",
            Protocol = "grpc",
            Service = "Svc",
            Method = "M",
            MethodType = "ClientStreaming",
            Status = "OK"
        };

        var status = await UnaryReplayer.ReplayAsync(
            ctx, step, new MockOptions(), NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(501, status);
        Assert.Contains("responseBinary", ReadBody(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrpcClientStreaming_MalformedResponseBinary_Returns501()
    {
        var (ctx, body) = NewCtx();
        var step = new BowireRecordingStep
        {
            Id = "step_cs_bad",
            Protocol = "grpc",
            Service = "Svc",
            Method = "M",
            MethodType = "ClientStreaming",
            Status = "OK",
            ResponseBinary = "###not-base64###"
        };

        var status = await UnaryReplayer.ReplayAsync(
            ctx, step, new MockOptions(), NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(501, status);
        Assert.Contains("malformed base64", ReadBody(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrpcBidi_NoReceivedMessages_Returns501()
    {
        var (ctx, body) = NewCtx();
        var step = new BowireRecordingStep
        {
            Id = "step_bidi_empty",
            Protocol = "grpc",
            Service = "Svc",
            Method = "M",
            MethodType = "Duplex",
            Status = "OK"
        };

        var status = await UnaryReplayer.ReplayAsync(
            ctx, step, new MockOptions(), NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(501, status);
        Assert.Contains("receivedMessages", ReadBody(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrpcBidi_FrameWithoutResponseBinary_Returns501()
    {
        var (ctx, body) = NewCtx();
        var step = new BowireRecordingStep
        {
            Id = "step_bidi_partial",
            Protocol = "grpc",
            Service = "Svc",
            Method = "M",
            MethodType = "Duplex",
            Status = "OK",
            ReceivedMessages = new List<BowireRecordingFrame>
            {
                new() { Index = 0, TimestampMs = 0, ResponseBinary = "AAAA" },
                new() { Index = 1, TimestampMs = 5, ResponseBinary = "" }
            }
        };

        var status = await UnaryReplayer.ReplayAsync(
            ctx, step, new MockOptions(), NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(501, status);
        Assert.Contains("responseBinary", ReadBody(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SseStep_NoReceivedMessages_Returns501ReRecordMessage()
    {
        var (ctx, body) = NewCtx();
        ctx.Request.Method = "GET";
        var step = new BowireRecordingStep
        {
            Id = "step_sse_empty",
            Protocol = "rest",
            Service = "Svc",
            Method = "M",
            MethodType = "ServerStreaming",
            HttpPath = "/events",
            HttpVerb = "GET",
            Status = "OK"
        };

        var status = await UnaryReplayer.ReplayAsync(
            ctx, step, new MockOptions(), NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(501, status);
        Assert.Contains("receivedMessages", ReadBody(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebSocketStep_NotAnUpgradeRequest_Returns501()
    {
        var (ctx, body) = NewCtx();
        var step = new BowireRecordingStep
        {
            Id = "step_ws",
            Protocol = "websocket",
            Service = "WebSocket",
            Method = "/ws",
            MethodType = "Duplex",
            HttpPath = "/ws",
            HttpVerb = "GET",
            Status = "OK"
        };

        var status = await UnaryReplayer.ReplayAsync(
            ctx, step, new MockOptions(), NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(501, status);
        Assert.Contains("WebSocket", ReadBody(body), StringComparison.Ordinal);
    }

    // ---- FormatSseFrame ----

    [Fact]
    public void FormatSseFrame_NonJsonPayload_FallsBackToLegacyShape()
    {
        var frame = UnaryReplayer.FormatSseFrame(7, "plain text");
        Assert.Equal("id: 7\ndata: plain text\n\n", frame);
    }

    [Fact]
    public void FormatSseFrame_JsonObjectWithoutDataField_FallsBackToLegacyShape()
    {
        var frame = UnaryReplayer.FormatSseFrame(3, """{"foo":1}""");
        // No `data` / `Data` property → not an SSE envelope; legacy id+data line.
        Assert.Contains("id: 3", frame, StringComparison.Ordinal);
        Assert.Contains("data: {\"foo\":1}", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSseFrame_MalformedJson_FallsBackToLegacyShape()
    {
        // Starts with '{' so the parser tries — JsonException → legacy fallback.
        var frame = UnaryReplayer.FormatSseFrame(0, "{not really json");
        Assert.Equal("id: 0\ndata: {not really json\n\n", frame);
    }

    [Fact]
    public void FormatSseFrame_EmptyPayload_FallsBackToLegacyShape()
    {
        var frame = UnaryReplayer.FormatSseFrame(5, "");
        Assert.Equal("id: 5\ndata: \n\n", frame);
    }

    // ---- FormatSocketIoEventFrame ----

    [Fact]
    public void FormatSocketIoEventFrame_NullData_ReturnsEmpty()
    {
        Assert.Equal("", UnaryReplayer.FormatSocketIoEventFrame(null, null));
    }

    [Fact]
    public void FormatSocketIoEventFrame_RawWireString_PassesThrough()
    {
        var raw = "42[\"event\",1]";
        Assert.Equal(raw, UnaryReplayer.FormatSocketIoEventFrame(raw, null));
    }

    [Fact]
    public void FormatSocketIoEventFrame_EnvelopeWithEventAndData_BuildsWireFrame()
    {
        // {event,data,timestamp} envelope: re-emitted as 42["e",<data>].
        var data = JsonSerializer.Deserialize<JsonElement>("""{"event":"tick","data":{"n":1}}""");
        var wire = UnaryReplayer.FormatSocketIoEventFrame(data, null);
        Assert.Equal("""42["tick",{"n":1}]""", wire);
    }

    [Fact]
    public void FormatSocketIoEventFrame_EnvelopeWithEventOnly_OmitsDataElement()
    {
        var data = JsonSerializer.Deserialize<JsonElement>("""{"event":"hello"}""");
        var wire = UnaryReplayer.FormatSocketIoEventFrame(data, "/ns");
        Assert.Equal("""42/ns,["hello"]""", wire);
    }

    [Fact]
    public void FormatSocketIoEventFrame_NonEnvelopeJsonObject_WrapsInMessageEvent()
    {
        // Object without `event` field → wrapped as ["message",<raw>].
        var data = JsonSerializer.Deserialize<JsonElement>("""{"foo":1}""");
        var wire = UnaryReplayer.FormatSocketIoEventFrame(data, null);
        Assert.Contains("\"message\"", wire, StringComparison.Ordinal);
        Assert.Contains("\"foo\":1", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSocketIoEventFrame_NonJsonString_WrappedAsJsonStringMessage()
    {
        // Plain text string that isn't already in 42[...] form → JSON-encoded
        // as the message event's payload.
        var wire = UnaryReplayer.FormatSocketIoEventFrame("just-a-string", null);
        Assert.StartsWith("42[\"message\",", wire, StringComparison.Ordinal);
    }

    // ---- PartitionSocketIoFrames ----

    [Fact]
    public void PartitionSocketIoFrames_NullFrames_ReturnsEmptyContainers()
    {
        var (broadcasts, acks) = UnaryReplayer.PartitionSocketIoFrames(null);
        Assert.Empty(broadcasts);
        Assert.Empty(acks);
    }

    [Fact]
    public void PartitionSocketIoFrames_AckEnvelope_GoesIntoQueue()
    {
        var frames = new List<BowireRecordingFrame>
        {
            new() { Index = 0, Data = JsonSerializer.Deserialize<JsonElement>("""{"event":"__ack__","data":["ok",42]}""") },
            new() { Index = 1, Data = JsonSerializer.Deserialize<JsonElement>("""{"event":"tick","data":{"n":1}}""") },
            new() { Index = 2, Data = JsonSerializer.Deserialize<JsonElement>("""{"event":"__ack__","data":"wrap-me"}""") },
            new() { Index = 3, Data = JsonSerializer.Deserialize<JsonElement>("""{"event":"__ack__"}""") }
        };

        var (broadcasts, acks) = UnaryReplayer.PartitionSocketIoFrames(frames);

        Assert.Single(broadcasts);
        Assert.Equal(3, acks.Count);
        Assert.Equal("[\"ok\",42]", acks.Dequeue()); // pre-wrapped array
        Assert.Equal("[\"wrap-me\"]", acks.Dequeue()); // scalar wrapped
        Assert.Equal("[]", acks.Dequeue()); // missing data → empty args
    }

    [Fact]
    public void PartitionSocketIoFrames_FrameWithNullData_GoesToBroadcasts()
    {
        var frames = new List<BowireRecordingFrame>
        {
            new() { Index = 0, Data = null }
        };
        var (broadcasts, acks) = UnaryReplayer.PartitionSocketIoFrames(frames);
        Assert.Single(broadcasts);
        Assert.Empty(acks);
    }

    [Fact]
    public void PartitionSocketIoFrames_StringFrameData_PassesThrough()
    {
        var frames = new List<BowireRecordingFrame>
        {
            new() { Index = 0, Data = """{"event":"__ack__","data":[true]}""" }
        };
        var (broadcasts, acks) = UnaryReplayer.PartitionSocketIoFrames(frames);
        Assert.Empty(broadcasts);
        Assert.Single(acks);
    }

    // ---- TryParseSocketIoEventWithAckId ----

    [Theory]
    [InlineData("42[\"e\"]", false, null, 0)]      // no ack id digits before bracket
    [InlineData("421[\"e\"]", true, null, 1)]      // default namespace, ack=1
    [InlineData("42/ns,7[\"e\"]", true, "/ns", 7)] // namespaced ack
    [InlineData("4", false, null, 0)]              // too short
    [InlineData("32[\"x\"]", false, null, 0)]      // not an event packet
    [InlineData("42/ns7[\"e\"]", false, null, 0)]  // no comma in namespace prefix
    [InlineData("42a[\"e\"]", false, null, 0)]     // non-digit before bracket
    public void TryParseSocketIoEventWithAckId_HandlesShapes(
        string frame, bool expectedOk, string? expectedNs, int expectedAck)
    {
        var ok = UnaryReplayer.TryParseSocketIoEventWithAckId(frame, out var ns, out var ackId);
        Assert.Equal(expectedOk, ok);
        if (expectedOk)
        {
            Assert.Equal(expectedNs, ns);
            Assert.Equal(expectedAck, ackId);
        }
    }

    // ---- TryExtractSocketIoBinaryFrame ----

    [Fact]
    public void TryExtractSocketIoBinaryFrame_NullData_ReturnsFalse()
    {
        Assert.False(UnaryReplayer.TryExtractSocketIoBinaryFrame(null, null, out _, out _));
    }

    [Fact]
    public void TryExtractSocketIoBinaryFrame_NoBinaryField_ReturnsFalse()
    {
        var data = JsonSerializer.Deserialize<JsonElement>("""{"event":"x","data":1}""");
        Assert.False(UnaryReplayer.TryExtractSocketIoBinaryFrame(data, null, out _, out _));
    }

    [Fact]
    public void TryExtractSocketIoBinaryFrame_EventWithBase64Bytes_BuildsHeaderAndBytes()
    {
        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var b64 = Convert.ToBase64String(bytes);
        var json = "{\"event\":\"img\",\"binary\":\"" + b64 + "\"}";
        var data = JsonSerializer.Deserialize<JsonElement>(json);

        var ok = UnaryReplayer.TryExtractSocketIoBinaryFrame(data, "/ns", out var header, out var binary);

        Assert.True(ok);
        Assert.NotNull(header);
        Assert.Equal(bytes, binary);
        Assert.Contains("/ns,", header, StringComparison.Ordinal);
        Assert.Contains("\"img\"", header, StringComparison.Ordinal);
        Assert.Contains("_placeholder", header, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractSocketIoBinaryFrame_EventWithBase64AndData_IncludesDataInHeader()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var b64 = Convert.ToBase64String(bytes);
        var json = "{\"event\":\"img\",\"binary\":\"" + b64 + "\",\"data\":{\"caption\":\"a\"}}";
        var data = JsonSerializer.Deserialize<JsonElement>(json);

        var ok = UnaryReplayer.TryExtractSocketIoBinaryFrame(data, null, out var header, out var binary);

        Assert.True(ok);
        Assert.Equal(bytes, binary);
        Assert.Contains("\"caption\":\"a\"", header, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractSocketIoBinaryFrame_EventWithMalformedBase64_ReturnsFalse()
    {
        var data = JsonSerializer.Deserialize<JsonElement>("""{"event":"img","binary":"@@@"}""");
        Assert.False(UnaryReplayer.TryExtractSocketIoBinaryFrame(data, null, out _, out _));
    }

    [Fact]
    public void TryExtractSocketIoBinaryFrame_StringRawData_StillParses()
    {
        // String that itself is the JSON envelope — the helper accepts any
        // representation that JsonDocument.Parse can swallow.
        var bytes = new byte[] { 9, 8, 7 };
        var raw = "{\"event\":\"img\",\"binary\":\"" + Convert.ToBase64String(bytes) + "\"}";
        var ok = UnaryReplayer.TryExtractSocketIoBinaryFrame(raw, null, out var header, out var binary);
        Assert.True(ok);
        Assert.Equal(bytes, binary);
        Assert.NotNull(header);
    }

    [Fact]
    public void TryExtractSocketIoBinaryFrame_NotJsonObject_ReturnsFalse()
    {
        Assert.False(UnaryReplayer.TryExtractSocketIoBinaryFrame("not json at all", null, out _, out _));
    }

    // ---- ResolveRecordedSubProtocol ----

    [Fact]
    public void ResolveRecordedSubProtocol_NoMetadata_ReturnsNull()
    {
        var ctx = new DefaultHttpContext();
        var step = new BowireRecordingStep { Id = "x", Metadata = null };
        Assert.Null(UnaryReplayer.ResolveRecordedSubProtocol(step, ctx));
    }

    [Fact]
    public void ResolveRecordedSubProtocol_MetadataWithoutSubprotocol_ReturnsNull()
    {
        var ctx = new DefaultHttpContext();
        var step = new BowireRecordingStep
        {
            Id = "x",
            Metadata = new Dictionary<string, string> { ["other"] = "value" }
        };
        Assert.Null(UnaryReplayer.ResolveRecordedSubProtocol(step, ctx));
    }

    [Fact]
    public void ResolveRecordedSubProtocol_EmptySubprotocolValue_ReturnsNull()
    {
        var ctx = new DefaultHttpContext();
        var step = new BowireRecordingStep
        {
            Id = "x",
            Metadata = new Dictionary<string, string> { ["_subprotocol"] = "" }
        };
        Assert.Null(UnaryReplayer.ResolveRecordedSubProtocol(step, ctx));
    }

    // ---- MapStatus / MapToGrpcStatus extra coverage ----

    [Theory]
    [InlineData("AlreadyExists", 409)]
    [InlineData("Aborted", 409)]
    [InlineData("OutOfRange", 400)]
    [InlineData("Internal", 500)]
    [InlineData("DataLoss", 500)]
    [InlineData("FailedPrecondition", 400)]
    [InlineData("Unknown", 500)]
    public void MapStatus_AllGrpcStatusFamily_Resolves(string input, int expected)
    {
        Assert.Equal(expected, UnaryReplayer.MapStatus(input));
    }

    [Theory]
    [InlineData("Cancelled", 1)]
    [InlineData("Unknown", 2)]
    [InlineData("DeadlineExceeded", 4)]
    [InlineData("AlreadyExists", 6)]
    [InlineData("PermissionDenied", 7)]
    [InlineData("ResourceExhausted", 8)]
    [InlineData("FailedPrecondition", 9)]
    [InlineData("Aborted", 10)]
    [InlineData("OutOfRange", 11)]
    [InlineData("Unimplemented", 12)]
    [InlineData("Internal", 13)]
    [InlineData("Unavailable", 14)]
    [InlineData("DataLoss", 15)]
    public void MapToGrpcStatus_AllNamedCodes_ProduceCanonicalNumber(string input, int expected)
    {
        Assert.Equal(expected, UnaryReplayer.MapToGrpcStatus(input).Code);
    }
}
