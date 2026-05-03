// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.Logging;
using MQTTnet.Server;

namespace Kuestenlogik.Bowire.Protocol.Mqtt.Mock;

/// <summary>
/// MQTT transport host for the Bowire mock server. Spins up an
/// embedded MQTTnet broker on its own port, then orchestrates the
/// proactive emitter (replays captured publishes) and the reactive
/// responder (matches incoming publishes against pattern-based
/// recordings and emits paired responses).
/// </summary>
/// <remarks>
/// Discovered as <see cref="IBowireMockTransportHost"/> via the
/// standard plugin-load pass. The mock server iterates every
/// registered host at startup, calls <see cref="ShouldStart"/> against
/// the loaded recording, and starts the ones that say yes. The MQTTnet
/// dependency lives entirely on this plugin — Kuestenlogik.Bowire.Mock has no
/// compile-time knowledge of MQTT after the plugin-isation refactor.
/// </remarks>
public sealed class MqttMockTransportHost : IBowireMockTransportHost, IAsyncDisposable
{
    private MqttServer? _broker;
    private MqttProactiveEmitter? _emitter;
    private MqttReactiveResponder? _reactive;

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    /// <inheritdoc/>
    public string Id => "mqtt";

    /// <inheritdoc/>
    public bool ShouldStart(BowireRecording recording) =>
        recording.Steps.Any(s =>
            string.Equals(s.Protocol, "mqtt", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(s.MethodType, "Unary", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(s.MethodType, "Duplex", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(s.MethodType, "ClientStreaming", StringComparison.OrdinalIgnoreCase)));

    /// <inheritdoc/>
    public async Task<int> StartAsync(BowireRecording recording, MockTransportContext context, CancellationToken ct)
    {
        // MQTTnet 5.1 doesn't expose the OS-assigned port from a
        // started server. If the caller asked for port 0, pick a free
        // one via a throwaway TcpListener first and pass that concrete
        // number to the broker. Small TOCTOU window; acceptable for
        // tests and an unlikely clash in practice.
        var chosenPort = context.RequestedPort;
        if (chosenPort == 0)
        {
            using var probe = new System.Net.Sockets.TcpListener(context.Host, 0);
            probe.Start();
            chosenPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        var factory = new MqttServerFactory();
        var brokerOptions = factory.CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(chosenPort)
            .WithDefaultEndpointBoundIPAddress(context.Host)
            .Build();

        _broker = factory.CreateMqttServer(brokerOptions);
        await _broker.StartAsync().ConfigureAwait(false);

        _emitter = new MqttProactiveEmitter(
            _broker, recording, context.ReplaySpeed, context.Logger, loop: context.Loop);
        _emitter.Start();

        // Reactive subscribe-match-respond. Only hooks the intercept
        // event when the recording has at least one Duplex /
        // ClientStreaming MQTT step — pure proactive recordings pay
        // nothing for this.
        _reactive = new MqttReactiveResponder(_broker, recording, context.Logger);
        _reactive.Start();

        context.Logger.LogInformation(
            "Bowire mock MQTT broker listening on mqtt://{Host}:{Port}",
            context.Host, chosenPort);

        return chosenPort;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken ct)
    {
        _reactive?.Dispose();
        _reactive = null;

        if (_emitter is not null)
        {
            await _emitter.DisposeAsync().ConfigureAwait(false);
            _emitter = null;
        }

        if (_broker is not null)
        {
            try
            {
                await _broker.StopAsync(new MqttServerStopOptions()).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Already stopped — accept and continue.
            }
            _broker.Dispose();
            _broker = null;
        }
    }
}
