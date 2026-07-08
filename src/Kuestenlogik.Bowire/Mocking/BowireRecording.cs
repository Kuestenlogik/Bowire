// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Security;

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

    /// <summary>
    /// Frozen snapshot of the effective frame-semantics annotations active
    /// at record-time — populated by Phase-5 captures so a replayed recording
    /// mounts the same widgets the original session showed, even if the local
    /// annotation store has drifted since. Optional: recordings made before
    /// Phase 5 leave this <c>null</c> and the workbench falls back to the
    /// live annotation store on load (same behaviour as pre-v1.3).
    /// </summary>
    [JsonPropertyName("schemaSnapshot")]
    public BowireRecordingSchemaSnapshot? SchemaSnapshot { get; set; }

    /// <summary>
    /// Original-schema sidecar — when set, the recording carries the source
    /// schema (OpenAPI YAML for REST, AsyncAPI YAML for messaging, &amp;c)
    /// that the live target advertised at capture time, plus the format tag
    /// the mock host needs to expose it under the right MIME type. Lets
    /// <see cref="IBowireMockHostingExtension"/>s serve the original
    /// contract back from the mock — so a peer Bowire discovery against
    /// the mock returns the <em>full</em> declared surface, not just the
    /// (smaller) set of methods this recording happens to replay. Use the
    /// step list to compute coverage; the recording reports the contract,
    /// the steps report the realised slice.
    /// </summary>
    /// <remarks>
    /// gRPC keeps its descriptor-set on each <see cref="BowireRecordingStep.SchemaDescriptor"/>
    /// because reflection wraps a binary <c>FileDescriptorSet</c> per
    /// service; this field carries a recording-wide text document for
    /// the protocols whose source schema is a single file (OpenAPI,
    /// AsyncAPI, GraphQL SDL, WSDL).
    /// </remarks>
    [JsonPropertyName("sourceSchema")]
    public RecordingSourceSchema? SourceSchema { get; set; }

    [JsonPropertyName("steps")]
    public IList<BowireRecordingStep> Steps { get; init; } = new List<BowireRecordingStep>();

    /// <summary>
    /// When <see langword="true"/>, this recording is a security-test
    /// probe (a "vulnerability template" in the
    /// <c>docs/architecture/security-testing.md</c> ADR), not a
    /// captured fixture. The <c>bowire scan</c> subcommand picks these
    /// up; the mock-server replay path explicitly skips them so an
    /// attack template never accidentally serves traffic. Optional;
    /// default <see langword="false"/> preserves backwards-compat with
    /// every pre-v1.4 recording.
    /// </summary>
    [JsonPropertyName("attack")]
    public bool Attack { get; set; }

    /// <summary>
    /// Identifying + classification metadata for the vulnerability the
    /// template probes for. Required when <see cref="Attack"/> is
    /// <see langword="true"/>, ignored otherwise.
    /// </summary>
    [JsonPropertyName("vulnerability")]
    public AttackVulnerability? Vulnerability { get; set; }

    /// <summary>
    /// Predicate-tree that, when matched against the response of the
    /// probe (the recording's first step), identifies the target as
    /// vulnerable. Required when <see cref="Attack"/> is <see langword="true"/>.
    /// </summary>
    [JsonPropertyName("vulnerableWhen")]
    public AttackPredicate? VulnerableWhen { get; set; }
}

/// <summary>
/// Sidecar carried at the top of a Phase-5+ recording file — the set of
/// effective annotations the workbench had resolved for the recorded
/// service+method pairs at record-time. The mock and the recording-replay
/// path use it to mount the same widgets the original session showed,
/// independent of the local annotation store's current state.
/// </summary>
/// <remarks>
/// Optional by design: a recording can omit <c>schemaSnapshot</c> entirely
/// (pre-Phase-5 captures, or a hardened workbench that doesn't share
/// annotations into recordings) and the loader treats this as "ask the live
/// store at replay time" — strictly backwards-compatible with v1.x
/// recordings.
/// </remarks>
public sealed class BowireRecordingSchemaSnapshot
{
    /// <summary>
    /// Annotations active at record-time, one entry per
    /// <c>(messageType, jsonPath)</c> combination per <c>(service, method)</c>
    /// pair. The wire-shape mirrors what the workbench's
    /// <c>GET /api/semantics/effective</c> endpoint returns, so the JS-side
    /// extension router can consume it without a separate path.
    /// </summary>
    [JsonPropertyName("annotations")]
    public IList<BowireRecordingSchemaAnnotation> Annotations { get; init; }
        = new List<BowireRecordingSchemaAnnotation>();
}

/// <summary>
/// One effective annotation snapshot inside a
/// <see cref="BowireRecordingSchemaSnapshot"/>. Matches the four-dimensional
/// addressing the frame-semantics framework uses, plus the resolved
/// <see cref="Semantic"/> string the workbench uses to pick a widget.
/// </summary>
public sealed class BowireRecordingSchemaAnnotation
{
    /// <summary>Service identifier (e.g. <c>"harbor.HarborService"</c>).</summary>
    [JsonPropertyName("service")]
    public string Service { get; set; } = "";

    /// <summary>Method identifier (e.g. <c>"WatchCrane"</c>).</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    /// <summary>
    /// Discriminator value — <c>"*"</c> for single-type methods, a concrete
    /// type name (<c>"EntityStatePdu"</c>) for multi-type channels.
    /// </summary>
    [JsonPropertyName("messageType")]
    public string MessageType { get; set; } = "*";

    /// <summary>JSONPath rooted at the message body (e.g. <c>$.position.lat</c>).</summary>
    [JsonPropertyName("jsonPath")]
    public string JsonPath { get; set; } = "";

    /// <summary>Resolved semantic kind-string (e.g. <c>"coordinate.latitude"</c>).</summary>
    [JsonPropertyName("semantic")]
    public string Semantic { get; set; } = "";
}

/// <summary>
/// The source-schema document a recording was captured from — carried
/// on <see cref="BowireRecording.SourceSchema"/> when the live target
/// advertised one. Lets the mock server serve the original contract back
/// (REST: <c>GET /openapi.json</c>, AsyncAPI: <c>GET /asyncapi.yaml</c>)
/// so peer discovery against the mock returns the full declared surface
/// rather than the slice the recording happens to cover.
/// </summary>
/// <param name="Format">
/// Schema format tag — <c>"openapi-3.0"</c>, <c>"openapi-2.0"</c>,
/// <c>"asyncapi-3.0"</c>, <c>"asyncapi-2.6"</c>, <c>"graphql-sdl"</c>,
/// <c>"wsdl-1.1"</c>, &amp;c. Mock-host extensions inspect this to decide
/// whether they're the right handler for this recording's schema.
/// </param>
/// <param name="Content">
/// Verbatim source-schema text (YAML or JSON, depending on
/// <paramref name="Format"/>). The mock host serves this byte-for-byte
/// under the matching endpoint.
/// </param>
/// <param name="SourceUrl">
/// Optional capture-time URL the schema came from (e.g.
/// <c>https://api.example.com/openapi.json</c>). Diagnostic only; not
/// used by the mock host. Lets a user pop a recording open and see
/// "this is the contract that lived at &lt;url&gt; when we captured."
/// </param>
public sealed record RecordingSourceSchema(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("sourceUrl")] string? SourceUrl = null);

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

    /// <summary>
    /// REST-only: response headers to re-emit on mock replay. Optional — when
    /// absent the replayer emits only the default <c>application/json</c>
    /// content type. Populated from a captured response (e.g. HAR import) or
    /// authored directly on a stub; a <c>Content-Type</c> entry here overrides
    /// the replayer's default. Framing headers Kestrel manages itself
    /// (Content-Length, Transfer-Encoding, Connection, …) are ignored on replay.
    /// </summary>
    [JsonPropertyName("responseHeaders")]
    public IDictionary<string, string>? ResponseHeaders { get; init; }

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

    /// <summary>
    /// #406: REST-only. When set, the response body is read from this file
    /// (WireMock's <c>bodyFileName</c>) instead of the inline <see cref="Response"/>
    /// — resolved relative to the recording's directory. Placeholder
    /// substitution still applies. A missing file falls back to <see cref="Response"/>.
    /// </summary>
    [JsonPropertyName("responseBodyFile")]
    public string? ResponseBodyFile { get; set; }

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

    /// <summary>
    /// Phase-5 discriminator-VALUE for this step — e.g. <c>"EntityStatePdu"</c>
    /// for a DIS PDU-type frame, or <c>"*"</c> when the method carries a
    /// single payload shape (no discriminator declared). Optional: pre-Phase-5
    /// recordings omit this and the framework treats the step as wildcard.
    /// </summary>
    /// <remarks>
    /// The field carries the resolved value, not the discriminator declaration —
    /// the declaration lives at the schema level (see
    /// <see cref="BowireRecordingSchemaSnapshot"/>).
    /// </remarks>
    [JsonPropertyName("discriminator")]
    public string? Discriminator { get; set; }

    /// <summary>
    /// Phase-5 interpretations — one entry per widget-mountable annotation
    /// active at record-time. Replay re-emits these alongside each frame so
    /// the workbench shows the same widget state regardless of how detector
    /// heuristics have drifted since capture. Optional: pre-Phase-5 captures
    /// have no <c>interpretations</c> field and the workbench falls back to
    /// running the live <see cref="Semantics.Detectors.IFrameProber"/> on the
    /// replayed frame — strictly backwards-compatible with v1.x recordings.
    /// </summary>
    [JsonPropertyName("interpretations")]
    public IList<RecordedInterpretation>? Interpretations { get; init; }

    /// <summary>
    /// REST-only: optional extra match predicates layered on top of the
    /// verb + path/template match (mock matching — <c>#402</c>). When present,
    /// <em>every</em> declared predicate must pass for the step to be a
    /// candidate, and <see cref="BowireStepMatch.Priority"/> breaks ties.
    /// Absent = the historical verb + path-only behaviour, so pre-#402
    /// recordings match exactly as before.
    /// </summary>
    [JsonPropertyName("match")]
    public BowireStepMatch? Match { get; init; }

    /// <summary>
    /// Named-scenario gating for this stub (mock matcher, <c>#408</c>). When
    /// set, the stub is eligible only while its scenario is in
    /// <see cref="BowireStepScenario.RequiredState"/>, and a successful match
    /// transitions the scenario to <see cref="BowireStepScenario.NewState"/>.
    /// Null = the stub is state-independent (the common case).
    /// </summary>
    [JsonPropertyName("scenario")]
    public BowireStepScenario? Scenario { get; init; }

    /// <summary>
    /// #407: when set, a matched stub forwards the request to this upstream
    /// base URL instead of replaying its recorded response — per-stub proxying
    /// for partial mocking. Null = replay the recorded response (the default).
    /// </summary>
    [JsonPropertyName("proxy")]
    public string? Proxy { get; init; }
}

/// <summary>
/// Named-scenario state-machine gating for a stub (mock matcher, <c>#408</c>) —
/// the analog of WireMock Scenarios. A scenario is a named state; a stub gated
/// on <see cref="RequiredState"/> only matches while the scenario is in that
/// state, and a match moves the scenario to <see cref="NewState"/>. Lets the
/// same request return different responses across a lifecycle (e.g.
/// not-found → created → found).
/// </summary>
public sealed class BowireStepScenario
{
    /// <summary>Scenario name. Multiple scenarios per recording are independent.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>
    /// State the scenario must be in for this stub to match. Null = the
    /// scenario's initial state (<c>Started</c>) — so a stub with a scenario
    /// name but no required state gates on the start state.
    /// </summary>
    [JsonPropertyName("requiredState")]
    public string? RequiredState { get; init; }

    /// <summary>State to move the scenario to after a successful match. Null = no transition.</summary>
    [JsonPropertyName("newState")]
    public string? NewState { get; init; }
}

/// <summary>
/// Extra REST match predicates + precedence for a recording step (mock
/// matcher, <c>#402</c>). All arms are optional; the matcher applies only
/// the ones set. A path pattern here (<see cref="PathRegex"/> /
/// <see cref="PathGlob"/>) overrides the step's <c>httpPath</c> template for
/// path matching; the query / header / cookie predicate lists all have to
/// pass; <see cref="Priority"/> orders steps that all match.
/// </summary>
public sealed class BowireStepMatch
{
    /// <summary>
    /// Regex the request path must match end-to-end (anchored automatically).
    /// Alternative to the step's <c>httpPath</c> literal / <c>{param}</c>
    /// template — use when a single stub should answer a family of paths.
    /// </summary>
    [JsonPropertyName("pathRegex")]
    public string? PathRegex { get; init; }

    /// <summary>
    /// Glob the request path must match: <c>*</c> matches within one segment
    /// (no <c>/</c>), <c>**</c> matches across segments, <c>?</c> matches one
    /// non-<c>/</c> character. Alternative to <see cref="PathRegex"/> /
    /// <c>httpPath</c>.
    /// </summary>
    [JsonPropertyName("pathGlob")]
    public string? PathGlob { get; init; }

    /// <summary>Predicates on query-string parameters — all must pass.</summary>
    [JsonPropertyName("query")]
    public IList<BowireMatchPredicate>? Query { get; init; }

    /// <summary>Predicates on request headers (name-insensitive) — all must pass.</summary>
    [JsonPropertyName("headers")]
    public IList<BowireMatchPredicate>? Headers { get; init; }

    /// <summary>Predicates on request cookies (from the <c>Cookie</c> header) — all must pass.</summary>
    [JsonPropertyName("cookies")]
    public IList<BowireMatchPredicate>? Cookies { get; init; }

    /// <summary>Predicates on the request body (<c>#403</c>) — all must pass.</summary>
    [JsonPropertyName("body")]
    public IList<BowireBodyMatcher>? Body { get; init; }

    /// <summary>
    /// Explicit precedence when several steps match the same request; higher
    /// wins, and dominates the matcher's implicit literal-beats-template /
    /// body-binding heuristics. Default <c>0</c>; negative values de-prioritise
    /// a fallback stub.
    /// </summary>
    [JsonPropertyName("priority")]
    public int Priority { get; init; }
}

/// <summary>
/// One name-scoped match predicate applied to a query parameter, header, or
/// cookie (mock matcher, <c>#402</c>). The name must be present unless
/// <see cref="Present"/> is <c>false</c>; when an operator
/// (<see cref="EqualTo"/> / <see cref="Matches"/> / <see cref="Contains"/>) is
/// set, at least one value must satisfy it. With only <see cref="Name"/> set
/// the predicate asserts mere presence.
/// </summary>
public sealed class BowireMatchPredicate
{
    /// <summary>Query-param / header / cookie name to test.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>Require a value exactly equal to this (respects <see cref="CaseInsensitive"/>).</summary>
    [JsonPropertyName("equals")]
    public string? EqualTo { get; init; }

    /// <summary>Require a value matching this regex.</summary>
    [JsonPropertyName("matches")]
    public string? Matches { get; init; }

    /// <summary>Require a value containing this substring (respects <see cref="CaseInsensitive"/>).</summary>
    [JsonPropertyName("contains")]
    public string? Contains { get; init; }

    /// <summary>
    /// <c>true</c> = the name must be present (the default when any operator is
    /// set); <c>false</c> = the name must be ABSENT (a negative predicate).
    /// </summary>
    [JsonPropertyName("present")]
    public bool? Present { get; init; }

    /// <summary>Case-insensitive comparison for <see cref="EqualTo"/> / <see cref="Contains"/> / <see cref="Matches"/>.</summary>
    [JsonPropertyName("caseInsensitive")]
    public bool CaseInsensitive { get; init; }
}

/// <summary>
/// A request-body match predicate (mock matcher, <c>#403</c>). Exactly one
/// mode applies, in this precedence:
/// <list type="number">
///   <item><see cref="JsonPath"/> set → navigate into the JSON body and apply
///     the text op (<see cref="EqualTo"/> / <see cref="Contains"/> /
///     <see cref="Matches"/>) to the value there, or — with no op — assert the
///     path is present (or, with <c>present:false</c>, absent).</item>
///   <item><see cref="EqualToJson"/> set → semantic JSON equality against the
///     whole body (order- and whitespace-insensitive), honouring
///     <see cref="IgnoreExtraElements"/> and <see cref="IgnoreArrayOrder"/>.</item>
///   <item>otherwise → the text op applies to the raw body string.</item>
/// </list>
/// XPath and JSON-schema body matching are not yet implemented (tracked as a
/// follow-up on <c>#403</c>).
/// </summary>
public sealed class BowireBodyMatcher
{
    /// <summary>Exact match (raw body, or the <see cref="JsonPath"/> value).</summary>
    [JsonPropertyName("equals")]
    public string? EqualTo { get; init; }

    /// <summary>Substring match (raw body, or the <see cref="JsonPath"/> value).</summary>
    [JsonPropertyName("contains")]
    public string? Contains { get; init; }

    /// <summary>Regex match (raw body, or the <see cref="JsonPath"/> value).</summary>
    [JsonPropertyName("matches")]
    public string? Matches { get; init; }

    /// <summary>Expected JSON (as text) for semantic whole-body equality.</summary>
    [JsonPropertyName("equalToJson")]
    public string? EqualToJson { get; init; }

    /// <summary>For <see cref="EqualToJson"/>: allow the actual body to carry properties the expected doesn't.</summary>
    [JsonPropertyName("ignoreExtraElements")]
    public bool IgnoreExtraElements { get; init; }

    /// <summary>For <see cref="EqualToJson"/>: compare arrays as multisets (order-independent).</summary>
    [JsonPropertyName("ignoreArrayOrder")]
    public bool IgnoreArrayOrder { get; init; }

    /// <summary>JSONPath into the body, e.g. <c>$.user.id</c> or <c>$.items[0].sku</c> (also accepts the dotted <c>user.id</c> form).</summary>
    [JsonPropertyName("jsonPath")]
    public string? JsonPath { get; init; }

    /// <summary>For <see cref="JsonPath"/> with no text op: <c>true</c> = path must exist, <c>false</c> = must NOT exist.</summary>
    [JsonPropertyName("present")]
    public bool? Present { get; init; }

    /// <summary>Case-insensitive comparison for the text ops.</summary>
    [JsonPropertyName("caseInsensitive")]
    public bool CaseInsensitive { get; init; }
}

/// <summary>
/// One captured interpretation inside a <see cref="BowireRecordingStep"/> —
/// the addressing path the parent-grouping uses, the resolved
/// <see cref="Kind"/> (the <see cref="Semantics.SemanticTag"/> value),
/// and the type-specific payload inlined as a free-form
/// <see cref="JsonElement"/> so the replay viewer doesn't have to
/// re-resolve from the raw frame.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Payload"/> shape is kind-specific by convention:
/// </para>
/// <list type="bullet">
///   <item><description>
///   <c>coordinate.wgs84</c> — <c>{ "lat": number, "lon": number }</c>.
///   </description></item>
///   <item><description>
///   <c>image.bytes</c> — <c>{ "data": "&lt;base64&gt;", "mimeType": "image/png" }</c>
///   (mimeType optional).
///   </description></item>
///   <item><description>
///   <c>audio.bytes</c> — <c>{ "data": "&lt;base64&gt;", "sampleRate": 44100 }</c>
///   (sampleRate optional).
///   </description></item>
/// </list>
/// <para>
/// The payload is open by design — a free-form <see cref="JsonElement"/>
/// — so the framework doesn't freeze itself into one widget's payload
/// shape this early. Widgets read the fields they need; unknown fields
/// flow through unchanged.
/// </para>
/// </remarks>
/// <param name="Kind">
/// The semantic kind-string this interpretation carries (e.g.
/// <c>"coordinate.wgs84"</c>). Matches the value the JS-side widget
/// router groups by.
/// </param>
/// <param name="Path">
/// JSONPath of the parent object the pairing logic groups under
/// (e.g. <c>$.position</c> for a lat/lon pair). The same path the
/// frame-semantics framework's pairing logic uses on the live path.
/// </param>
/// <param name="Payload">
/// Kind-specific payload as inlined data. Carried as a
/// <see cref="JsonElement"/> so the on-disk shape, the wire shape, and
/// the C# model agree byte-for-byte across save/load cycles.
/// </param>
public sealed record RecordedInterpretation(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("payload")] JsonElement Payload);

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

    /// <summary>
    /// Phase-5 discriminator value for this frame — <c>"*"</c> for
    /// single-type methods, a concrete type name for multi-type channels.
    /// Optional: pre-Phase-5 captures omit the field and the framework
    /// treats the frame as wildcard.
    /// </summary>
    [JsonPropertyName("discriminator")]
    public string? Discriminator { get; set; }

    /// <summary>
    /// Phase-5 interpretations captured for this frame. Replay re-emits
    /// these verbatim instead of re-running detection — see
    /// <see cref="RecordingReplayInterpretationResolver"/>. Optional;
    /// pre-Phase-5 frames omit the field.
    /// </summary>
    [JsonPropertyName("interpretations")]
    public IList<RecordedInterpretation>? Interpretations { get; init; }
}
