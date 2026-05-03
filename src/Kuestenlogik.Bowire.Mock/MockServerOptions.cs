// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mock.Chaos;
using Kuestenlogik.Bowire.Mock.Matchers;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mock;

/// <summary>
/// Tunables for the standalone <see cref="MockServer"/> used by the
/// <c>bowire mock</c> CLI subcommand.
/// </summary>
public sealed class MockServerOptions
{
    /// <summary>
    /// Path to the recording JSON file. Required unless
    /// <see cref="SchemaPath"/> is set (schema-only mock, Phase 3d).
    /// Exactly one of <c>RecordingPath</c> and <c>SchemaPath</c> must be
    /// supplied; <see cref="MockServer.StartAsync"/> validates this.
    /// </summary>
    public string? RecordingPath { get; init; }

    /// <summary>
    /// Path to an OpenAPI 3 document (JSON or YAML). When set, the mock
    /// generates plausible responses directly from the schema without
    /// any recorded traffic (Phase 3d). Exclusive with
    /// <see cref="RecordingPath"/>.
    /// </summary>
    public string? SchemaPath { get; init; }

    /// <summary>
    /// Path to a protobuf <c>FileDescriptorSet</c> binary (the output
    /// of <c>protoc --descriptor_set_out=... --include_imports</c>).
    /// When set, the mock synthesises a recording where every gRPC
    /// method gets a sample response encoded on the fly via
    /// <c>ProtobufSampleEncoder</c>. Same mutual-
    /// exclusion rules as <see cref="SchemaPath"/>: exactly one of
    /// <see cref="RecordingPath"/>, <see cref="SchemaPath"/>,
    /// <see cref="GrpcSchemaPath"/>, or
    /// <see cref="GraphQlSchemaPath"/> must be set.
    /// </summary>
    public string? GrpcSchemaPath { get; init; }

    /// <summary>
    /// Path to a GraphQL SDL file. When set, the mock answers every
    /// <c>POST /graphql</c> request by parsing the incoming query,
    /// walking the schema for each selection-set field, and returning
    /// a sample-valued JSON response shaped to match. Unlike the other
    /// schema-only modes the response is synthesised per request (not
    /// pre-generated) because GraphQL responses are selection-set-
    /// dependent. Mutex with <see cref="RecordingPath"/>,
    /// <see cref="SchemaPath"/>, and <see cref="GrpcSchemaPath"/>.
    /// </summary>
    public string? GraphQlSchemaPath { get; init; }

    /// <summary>
    /// Bind address — defaults to <c>127.0.0.1</c> so the mock is reachable
    /// only from the local machine. Set to <c>0.0.0.0</c> to expose on the
    /// LAN (e.g. for sidecar-container setups).
    /// </summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>TCP port to listen on.</summary>
    public int Port { get; init; } = 6000;

    /// <summary>
    /// Disambiguator when <see cref="RecordingPath"/> points at a file with
    /// multiple recordings. Either a recording name or id.
    /// </summary>
    public string? Select { get; init; }

    /// <summary>Watch the recording file for changes. Defaults to <c>true</c>.</summary>
    public bool Watch { get; init; } = true;

    /// <summary>
    /// Matcher for the middleware. Defaults to <see cref="ExactMatcher"/>.
    /// </summary>
    public IMockMatcher Matcher { get; init; } = new ExactMatcher();

    /// <summary>
    /// Optional logger factory. When <c>null</c>, the server uses a default
    /// console factory.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Speed multiplier for streaming replay (Phase 2c onwards). Forwarded
    /// to <see cref="MockOptions.ReplaySpeed"/> on the embedded middleware.
    /// <c>1.0</c> preserves the recorded cadence; <c>0</c> emits every
    /// frame immediately; <c>2.0</c> is twice as fast.
    /// </summary>
    public double ReplaySpeed { get; init; } = 1.0;

    /// <summary>
    /// Per-transport bind ports keyed by <see cref="IBowireMockTransportHost.Id"/>
    /// (e.g. <c>"mqtt"</c> → <c>1883</c>). The mock server passes the
    /// configured port into the matching transport host's
    /// <see cref="IBowireMockTransportHost.StartAsync"/> via
    /// <see cref="MockTransportContext.RequestedPort"/>. Missing keys
    /// default to <c>0</c> (OS-assigned). Hosts whose id isn't present
    /// here also get <c>0</c>.
    /// </summary>
    public IReadOnlyDictionary<string, int> TransportPorts { get; init; }
        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Plugin-contributed transport hosts (MQTT broker, AMQP broker,
    /// DDS participant, ...). The mock server iterates this list at
    /// startup, calls <see cref="IBowireMockTransportHost.ShouldStart"/>
    /// per candidate, and starts the ones that claim the recording.
    /// Hosts are stopped when the mock shuts down.
    /// <para>
    /// Discovered alongside <see cref="Emitters"/> via the standard
    /// plugin-load pass. Empty by default — populate from a plugin
    /// directory in your wiring code (the bowire CLI does this
    /// automatically). Replaced the dedicated <c>EnableMqtt</c> /
    /// <c>MqttPort</c> properties when MQTT moved out of
    /// <c>Kuestenlogik.Bowire.Mock</c> into the
    /// <c>Kuestenlogik.Bowire.Protocol.Mqtt</c> plugin.
    /// </para>
    /// </summary>
    public IReadOnlyList<IBowireMockTransportHost> TransportHosts { get; init; }
        = Array.Empty<IBowireMockTransportHost>();

    /// <summary>
    /// Plugin-contributed schema-to-recording converters for the
    /// <c>--schema</c> / <c>--grpc-schema</c> / <c>--graphql-schema</c>
    /// modes. The mock server picks the entry whose
    /// <see cref="IBowireMockSchemaSource.Kind"/> matches the
    /// configured schema kind (<c>"openapi"</c> / <c>"protobuf"</c> /
    /// <c>"graphql"</c>) and delegates the load.
    /// </summary>
    public IReadOnlyList<IBowireMockSchemaSource> SchemaSources { get; init; }
        = Array.Empty<IBowireMockSchemaSource>();

    /// <summary>
    /// Plugin-contributed live-schema handlers — middleware-style
    /// request handlers that some schema kinds (GraphQL today) need
    /// because their responses are dispatch-time-dependent. The mock
    /// server registers the handler whose
    /// <see cref="IBowireMockLiveSchemaHandler.Kind"/> matches the
    /// configured schema kind in front of the recording-replay
    /// middleware.
    /// </summary>
    public IReadOnlyList<IBowireMockLiveSchemaHandler> LiveSchemaHandlers { get; init; }
        = Array.Empty<IBowireMockLiveSchemaHandler>();

    /// <summary>
    /// Plugin-contributed hosting extensions — services + endpoints +
    /// HTTP-2-requirement based on the loaded recording. gRPC plugin
    /// uses this to add <c>AddGrpc()</c> + <c>ReflectionServiceImpl</c>
    /// and to map the gRPC reflection endpoint when the recording has
    /// gRPC steps with attached descriptors.
    /// </summary>
    public IReadOnlyList<IBowireMockHostingExtension> HostingExtensions { get; init; }
        = Array.Empty<IBowireMockHostingExtension>();

    /// <summary>
    /// Chaos-injection tunables forwarded to the embedded middleware
    /// (Phase 3a). Populated from the <c>--chaos</c> CLI flag via
    /// <see cref="ChaosOptions.Parse"/> or set programmatically. Defaults
    /// are off.
    /// </summary>
    public ChaosOptions Chaos { get; init; } = new();

    /// <summary>
    /// When <c>true</c>, the mock steps through the recording in strict
    /// order (Phase 3b). CLI: <c>--stateful</c>. Defaults to <c>false</c>.
    /// </summary>
    public bool Stateful { get; init; }

    /// <summary>
    /// When <see cref="Stateful"/> is on, wrap back to step 0 after the
    /// last step (default) or return a miss for every subsequent request
    /// when <c>false</c>. CLI: <c>--stateful-once</c> sets this to
    /// <c>false</c>.
    /// </summary>
    public bool StatefulWrapAround { get; init; } = true;

    /// <summary>
    /// When set, unmatched REST requests are appended as placeholder steps
    /// to the named file (Phase 3c). CLI: <c>--capture-miss &lt;path&gt;</c>.
    /// </summary>
    public string? CaptureMissPath { get; init; }

    /// <summary>
    /// Shared secret that unlocks the runtime scenario-switch control
    /// endpoint (<c>POST /__bowire/mock/scenario</c>). When
    /// <c>null</c> (default) the control surface is 404ed. CLI:
    /// <c>--control-token &lt;value&gt;</c>.
    /// </summary>
    public string? ControlToken { get; init; }

    /// <summary>
    /// When <c>true</c>, the MQTT proactive emitter replays the
    /// recording on repeat while the mock is up — the schedule loops
    /// back to step 0 after emitting the last publish. Useful for
    /// long-running demos with a static sensor stream. Default stays
    /// one-shot (emitter stops after the last step) so integration
    /// tests don't observe phantom repeats. CLI: <c>--loop</c>.
    /// </summary>
    public bool Loop { get; init; }

    /// <summary>
    /// Extension point for plugin-contributed broadcast emitters
    /// (DIS, DDS, raw-UDP multicast, ...). The mock server iterates
    /// this list after HTTP startup, calls
    /// <see cref="IBowireMockEmitter.CanEmit"/> per candidate,
    /// and starts the ones that claim the recording. Emitters are
    /// disposed when the mock shuts down. Default is empty —
    /// protocols that live inside <c>Kuestenlogik.Bowire.Mock</c> (MQTT) wire
    /// up through dedicated paths rather than this list so legacy
    /// behaviour stays unchanged.
    /// </summary>
    public IReadOnlyList<IBowireMockEmitter> Emitters { get; init; }
        = Array.Empty<IBowireMockEmitter>();
}
