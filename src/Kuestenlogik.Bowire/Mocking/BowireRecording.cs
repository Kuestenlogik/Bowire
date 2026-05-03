// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// On-disk shape of <c>~/.bowire/recordings.json</c> — a container for one
/// or more <see cref="BowireRecording"/> instances. The mock server accepts
/// both this wrapper ("full store" file) and a single <see cref="BowireRecording"/>
/// at the top level (single-scenario file).
/// </summary>
public sealed class BowireRecordingStore
{
    [JsonPropertyName("recordings")]
    public IList<BowireRecording> Recordings { get; init; } = new List<BowireRecording>();
}

/// <summary>
/// One named recording — an ordered sequence of captured invocations produced
/// by the Bowire UI's recorder and later replayed by the mock server.
/// </summary>
public sealed class BowireRecording
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }

    /// <summary>
    /// Format version the recording was written with. The mock-server loader
    /// refuses any version it wasn't built for.
    /// </summary>
    [JsonPropertyName("recordingFormatVersion")]
    public int? RecordingFormatVersion { get; set; }

    [JsonPropertyName("steps")]
    public IList<BowireRecordingStep> Steps { get; init; } = new List<BowireRecordingStep>();
}

/// <summary>
/// One captured invocation inside a <see cref="BowireRecording"/>.
/// Mirrors the payload emitted by <c>captureRecordingStep()</c> in the
/// Bowire UI's recording.js.
/// </summary>
public sealed class BowireRecordingStep
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("capturedAt")]
    public long CapturedAt { get; set; }

    /// <summary>Protocol source (e.g. <c>rest</c>, <c>grpc</c>, <c>signalr</c>).</summary>
    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "";

    /// <summary>Service identifier — OpenAPI tag, gRPC service FQN, hub name, ...</summary>
    [JsonPropertyName("service")]
    public string Service { get; set; } = "";

    /// <summary>Method identifier — operationId, gRPC method name, hub method, ...</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    /// <summary><c>Unary</c>, <c>ServerStreaming</c>, <c>ClientStreaming</c>, or <c>Duplex</c>.</summary>
    [JsonPropertyName("methodType")]
    public string MethodType { get; set; } = "Unary";

    [JsonPropertyName("serverUrl")]
    public string? ServerUrl { get; set; }

    /// <summary>Primary request body (for unary: the full request; for streaming: the first message).</summary>
    [JsonPropertyName("body")]
    public string? Body { get; set; }

    /// <summary>All request messages in order (single entry for unary; multiple for streaming).</summary>
    [JsonPropertyName("messages")]
    public IList<string> Messages { get; init; } = new List<string>();

    /// <summary>Request metadata / headers (including auth-helper output).</summary>
    [JsonPropertyName("metadata")]
    public IDictionary<string, string>? Metadata { get; init; }

    /// <summary>Status string — <c>OK</c>, HTTP code name, gRPC status code, ...</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "OK";

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    /// <summary>
    /// Response body (for unary: the full response; for server-streaming: the
    /// last frame — the replayer uses <c>receivedMessages</c> instead and
    /// ignores this for streaming steps).
    /// </summary>
    [JsonPropertyName("response")]
    public string? Response { get; set; }

    /// <summary>REST-only: the HTTP path template the call was made against.</summary>
    [JsonPropertyName("httpPath")]
    public string? HttpPath { get; set; }

    /// <summary>REST-only: the HTTP verb the call was made with.</summary>
    [JsonPropertyName("httpVerb")]
    public string? HttpVerb { get; set; }

    /// <summary>
    /// gRPC-only: base64-encoded raw wire bytes of the response message.
    /// Captured so mock replay can re-emit the response byte-for-byte
    /// without a runtime protobuf encoder. Populated at recording time
    /// from the protocol plugin's <c>InvokeResult.ResponseBinary</c>
    /// and requires <c>recordingFormatVersion: 2</c>.
    /// </summary>
    [JsonPropertyName("responseBinary")]
    public string? ResponseBinary { get; set; }

    /// <summary>
    /// gRPC-only: base64-encoded raw wire bytes of the first request
    /// message (length-prefix stripped). Written by the miss-capture
    /// writer so the user can inspect what the client sent when the
    /// matcher found no step; the replayer doesn't consume it.
    /// </summary>
    [JsonPropertyName("requestBinary")]
    public string? RequestBinary { get; set; }

    /// <summary>
    /// gRPC-only: base64-encoded protobuf <c>FileDescriptorSet</c> covering
    /// this service's schema (plus its transitive deps). Captured by the
    /// gRPC plugin at discovery time via Server Reflection, attached to
    /// every step from the same service. Consumed by the mock to expose
    /// its own Server Reflection so a second Bowire workbench can
    /// auto-discover the mocked services.
    /// </summary>
    [JsonPropertyName("schemaDescriptor")]
    public string? SchemaDescriptor { get; set; }

    /// <summary>
    /// Duplex / client-streaming: client-to-server messages with relative
    /// timestamps. Streaming replay uses these to reproduce the send cadence.
    /// </summary>
    [JsonPropertyName("sentMessages")]
    public IList<BowireRecordingFrame>? SentMessages { get; init; }

    /// <summary>
    /// Streaming / duplex: server-to-client frames with per-frame timestamps.
    /// Streaming replay uses these to emit frames at the original cadence.
    /// </summary>
    [JsonPropertyName("receivedMessages")]
    public IList<BowireRecordingFrame>? ReceivedMessages { get; init; }
}

/// <summary>
/// One frame inside a streaming or duplex recording step — carries the frame
/// payload plus the timestamp offset from the stream start, so streaming
/// replay can pace emission at the original cadence.
/// </summary>
public sealed class BowireRecordingFrame
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("timestampMs")]
    public long? TimestampMs { get; set; }

    [JsonPropertyName("data")]
    public object? Data { get; set; }

    /// <summary>For sent frames: the message body the client transmitted.</summary>
    [JsonPropertyName("body")]
    public string? Body { get; set; }

    /// <summary>
    /// gRPC server-streaming only: base64-encoded raw wire bytes of this
    /// frame's protobuf payload. Consumed by streaming mock replay so each
    /// streamed frame emits 1:1 on the wire without re-encoding.
    /// </summary>
    [JsonPropertyName("responseBinary")]
    public string? ResponseBinary { get; set; }
}
