// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.App.Configuration;

/// <summary>
/// Typed configuration for the <c>bowire mock</c> subcommand. Bound from
/// the <c>Bowire:Mock</c> section of the shared
/// <see cref="BowireConfiguration"/> stack so users can pin defaults in
/// <c>appsettings.json</c>, override via <c>BOWIRE_*</c> env vars, and
/// retype CLI flags for one-off runs — standard .NET precedence in all
/// three layers.
/// </summary>
/// <remarks>
/// <para>
/// Config shape:
/// </para>
/// <code>
/// {
///   "Bowire": {
///     "Mock": {
///       "Port": 6000,
///       "Host": "127.0.0.1",
///       "RecordingPath": "./happy.json",
///       "Stateful": false,
///       "Chaos": "latency:50-200,fail-rate:0.05"
///     }
///   }
/// }
/// </code>
/// <para>
/// CLI flags map to these keys via
/// <c>BowireCli</c>' switch mappings
/// (<c>--recording</c> → <c>RecordingPath</c>, <c>--port</c> →
/// <c>Port</c>, …). Bare toggles (<c>--no-watch</c>, <c>--stateful</c>,
/// <c>--stateful-once</c>) are expanded to <c>--flag true</c> by the
/// bootstrap's boolean-flag preprocessor.
/// </para>
/// </remarks>
internal sealed class MockCliOptions
{
    /// <summary>Path to a <c>.bwr</c> recording file. Mutex with <see cref="SchemaPath"/>.</summary>
    public string? RecordingPath { get; set; }

    /// <summary>Path to an OpenAPI 3 document. Mutex with <see cref="RecordingPath"/>.</summary>
    public string? SchemaPath { get; set; }

    /// <summary>Path to a protobuf <c>FileDescriptorSet</c> binary. Mutex with <see cref="RecordingPath"/> + <see cref="SchemaPath"/>.</summary>
    public string? GrpcSchemaPath { get; set; }

    /// <summary>Path to a GraphQL SDL file. Mutex with <see cref="RecordingPath"/>, <see cref="SchemaPath"/>, and <see cref="GrpcSchemaPath"/>.</summary>
    public string? GraphQlSchemaPath { get; set; }

    /// <summary>Bind host. Defaults to localhost; set <c>0.0.0.0</c> for LAN.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Listen port. Defaults to 6000; <c>0</c> asks the OS to pick.</summary>
    public int Port { get; set; } = 6000;

    /// <summary>Disambiguator when the recording file contains multiple recordings.</summary>
    public string? Select { get; set; }

    /// <summary>Raw chaos spec (e.g. <c>"latency:100-500,fail-rate:0.05"</c>). Parsed by <c>ChaosOptions.Parse</c>.</summary>
    public string? Chaos { get; set; }

    /// <summary>Target file for captured misses (Phase 3c).</summary>
    public string? CaptureMissPath { get; set; }

    /// <summary>Suppress hot-reload on file changes. Translates to <c>MockServerOptions.Watch = false</c>.</summary>
    public bool NoWatch { get; set; }

    /// <summary>Walk the recording in strict order (Phase 3b).</summary>
    public bool Stateful { get; set; }

    /// <summary>Like <see cref="Stateful"/> but stops after the last step instead of wrapping.</summary>
    public bool StatefulOnce { get; set; }

    /// <summary>
    /// Playback speed multiplier for streaming replay (Phase 2c onwards).
    /// <c>1.0</c> (default) preserves the cadence captured on the per-
    /// frame <c>timestampMs</c>; <c>2.0</c> is twice as fast; <c>0</c>
    /// emits every frame immediately. Bound from <c>--speed</c> and
    /// <c>Bowire:Mock:ReplaySpeed</c>.
    /// </summary>
    public double ReplaySpeed { get; set; } = 1.0;

    /// <summary>
    /// Shared secret that unlocks the runtime scenario-switch control
    /// endpoint (<c>POST /__bowire/mock/scenario</c>). When
    /// <c>null</c> (default) the control surface is invisible (404 for
    /// every request to <c>/__bowire/mock/*</c>). Bound from
    /// <c>--control-token</c> / <c>Bowire:Mock:ControlToken</c>.
    /// </summary>
    public string? ControlToken { get; set; }

    /// <summary>
    /// When <c>true</c>, the MQTT proactive emitter replays the
    /// recording on repeat (default is one-shot). Bound from
    /// <c>--loop</c> / <c>Bowire:Mock:Loop</c>.
    /// </summary>
    public bool Loop { get; set; }

    /// <summary>
    /// When <c>true</c>, <c>bowire mock</c> resolves and installs any
    /// protocol plugin the recording references but the host doesn't
    /// have, then continues. Without the flag, the same situation is a
    /// fail-fast with install hints (exit 1). Bound from
    /// <c>--auto-install</c> / <c>Bowire:Mock:AutoInstall</c>.
    /// </summary>
    public bool AutoInstall { get; set; }
}
