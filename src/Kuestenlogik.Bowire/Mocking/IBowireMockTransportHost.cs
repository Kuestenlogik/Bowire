// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// Plugin-contributed transport host that the Bowire mock server starts
/// alongside its main HTTP listener — for protocols that need their own
/// listener on a separate port (MQTT-broker today, AMQP / DDS / NNG /
/// raw-socket later). Plugins implement this in their own assembly so
/// the heavy transport library (e.g. MQTTnet) hangs only on the
/// protocol-plugin package, not on Kuestenlogik.Bowire.Mock.
/// </summary>
/// <remarks>
/// <para>
/// Discovered alongside <see cref="IBowireMockEmitter"/> via the
/// existing plugin-load pass; <c>MockServerOptions.TransportHosts</c>
/// receives every concrete instance the host's plugin directory yields.
/// The mock server iterates them at startup, asks each whether the
/// loaded recording has relevant steps via <see cref="ShouldStart"/>,
/// and starts the ones that say yes.
/// </para>
/// <para>
/// The lifetime contract:
/// <list type="number">
///   <item><see cref="StartAsync"/> is called once during MockServer
///         startup, after the recording is loaded but before the HTTP
///         listener accepts traffic. Return the port the transport
///         actually bound (or 0 when the transport doesn't have a
///         meaningful port number).</item>
///   <item><see cref="StopAsync"/> is called once during MockServer
///         shutdown. Hosts must shut their transport down cleanly so
///         OS-level ports release for the next test run.</item>
/// </list>
/// </para>
/// </remarks>
public interface IBowireMockTransportHost
{
    /// <summary>
    /// Stable transport id surfaced in logs and in
    /// <c>MockServer.TransportPorts</c>. Use the same id the recording
    /// step's <c>protocol</c> field carries, lower-case (e.g.
    /// <c>"mqtt"</c>, <c>"amqp"</c>).
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Decide whether <see cref="StartAsync"/> should be called for the
    /// given recording — typically by scanning for steps whose
    /// <c>protocol</c> matches <see cref="Id"/>. Hosts that return
    /// <c>false</c> are skipped silently (no port allocation, no log
    /// noise).
    /// </summary>
    bool ShouldStart(BowireRecording recording);

    /// <summary>
    /// Start the transport listener. Implementations are responsible
    /// for binding the port (using <see cref="MockTransportContext.RequestedPort"/>
    /// when non-zero, or picking an OS-assigned port when zero) and
    /// returning the actually-bound port number.
    /// </summary>
    Task<int> StartAsync(BowireRecording recording, MockTransportContext context, CancellationToken ct);

    /// <summary>
    /// Stop the transport. Called during MockServer shutdown — close
    /// listeners, release resources, drop scheduled tasks.
    /// </summary>
    Task StopAsync(CancellationToken ct);
}

/// <summary>
/// Run-time context handed to every <see cref="IBowireMockTransportHost"/>
/// at startup — host bind address, requested port, and the playback
/// knobs (replay speed, loop) that affect proactive emission inside the
/// transport. Per-transport-id; the mock server constructs a separate
/// instance for each registered host.
/// </summary>
/// <param name="Host">IP address (parsed) the main HTTP listener bound to.</param>
/// <param name="RequestedPort">Port the user asked for via CLI / options; 0 means OS-assigned.</param>
/// <param name="ReplaySpeed">Playback speed multiplier — 1.0 preserves cadence, 0 emits everything immediately.</param>
/// <param name="Loop">When <c>true</c>, proactive transports replay their captured stream indefinitely.</param>
/// <param name="Logger">Logger scoped to the mock server; transports should log under it for unified output.</param>
public sealed record MockTransportContext(
    System.Net.IPAddress Host,
    int RequestedPort,
    double ReplaySpeed,
    bool Loop,
    ILogger Logger);
