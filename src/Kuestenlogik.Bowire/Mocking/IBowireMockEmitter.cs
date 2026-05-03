// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// Extension point for protocol plugins that need to contribute
/// broadcast-style replay behaviour to the mock server — protocols
/// whose wire model is "server pushes on its own schedule" rather
/// than "client makes a request". MQTT and DIS are the reference
/// implementations (the MQTT emitter is built into the mock server;
/// DIS lives in its own plugin).
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle:
/// </para>
/// <list type="number">
///   <item>Host builds a mock-server configuration with the
///   plugin-provided emitters attached.</item>
///   <item>After HTTP startup, the mock server calls
///   <see cref="CanEmit"/> on each emitter; the ones that claim the
///   recording get <see cref="StartAsync"/> invoked.</item>
///   <item>On mock-server shutdown each started emitter is disposed
///   via <see cref="IAsyncDisposable"/>.</item>
/// </list>
/// <para>
/// Emitters run for the entire lifetime of the mock server; they
/// typically own their own network listeners (UDP multicast socket,
/// MQTT broker, ...) so the mock host doesn't need to learn every
/// protocol's transport. Dispatch goes through the plugin — the
/// mock package takes no compile-time dependency on any emitter's
/// protocol stack. Symmetrically, the plugin takes no compile-time
/// dependency on the mock server: this interface is declared in the
/// core <c>Kuestenlogik.Bowire</c> contract package.
/// </para>
/// </remarks>
public interface IBowireMockEmitter : IAsyncDisposable
{
    /// <summary>
    /// Short identifier for logging and diagnostics (e.g. <c>"dis"</c>,
    /// <c>"dds"</c>). Typically matches the protocol plugin's
    /// <c>IBowireProtocol.Id</c> so logs correlate cleanly.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Decide whether this emitter should run for the given recording.
    /// Return <c>true</c> when at least one step in the recording
    /// targets this emitter's protocol; the mock server skips emitters
    /// whose recording has no relevant steps so empty UDP sockets
    /// don't get bound for nothing.
    /// </summary>
    bool CanEmit(BowireRecording recording);

    /// <summary>
    /// Start the emitter. Called once per mock-server run, after
    /// Kestrel has bound its HTTP listener and any built-in emitters
    /// have started. The emitter owns its transport and schedule;
    /// this call should return once the transport is ready to send
    /// (not when the emission is complete).
    /// </summary>
    Task StartAsync(
        BowireRecording recording,
        MockEmitterOptions options,
        ILogger logger,
        CancellationToken ct);
}
