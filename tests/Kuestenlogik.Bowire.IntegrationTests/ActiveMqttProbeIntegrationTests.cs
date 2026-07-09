// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Kuestenlogik.Bowire.Protocol.Mqtt;
using Kuestenlogik.Bowire.Security.Scanner;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end proof for the active MQTT retained-poisoning probe (#395):
/// drives the real <see cref="BowireMqttProtocol"/> plugin against an in-process
/// MQTTnet broker (which persists retained messages by default), so the probe's
/// PUBLISH-retained → fresh-SUBSCRIBE → observe path is verified against a live
/// broker rather than a fake. Also asserts the probe cleans up after itself
/// (the retained message is deleted).
/// </summary>
public sealed class ActiveMqttProbeIntegrationTests : IAsyncLifetime
{
    private MqttServer? _broker;
    private int _brokerPort;

    public async ValueTask InitializeAsync()
    {
        _brokerPort = FindFreeTcpPort();
        var factory = new MqttServerFactory();
        _broker = factory.CreateMqttServer(
            new MqttServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointPort(_brokerPort)
                .Build());
        await _broker.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_broker is not null)
        {
            await _broker.StopAsync();
            _broker.Dispose();
        }
    }

    [Fact]
    public async Task RetainedPoisoning_LiveBroker_FlagsVulnerable_ThenClearsRetained()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new MqttRetainedPoisoningProbe();
        var protocol = new BowireMqttProtocol();
        var target = $"mqtt://localhost:{_brokerPort}";

        // A default MQTTnet broker persists + re-delivers retained messages, so
        // the probe should flag the poisoning vector against it.
        var findings = await probe.RunAsync(target, protocol, ["Authorization: Bearer x"], new ActiveScanOptions(), ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API8-MQTT-RETAINED-POISONING", f.Template.Recording.Vulnerability?.Id);

        // Cleanup verification: a brand-new subscriber to the probe's topic space
        // must NOT receive a retained message — the probe cleared it (empty
        // retained payload deletes retained state per MQTT semantics).
        var got = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientFactory = new MqttClientFactory();
        using var subscriber = clientFactory.CreateMqttClient();
        subscriber.ApplicationMessageReceivedAsync += _ => { got.TrySetResult(true); return Task.CompletedTask; };
        await subscriber.ConnectAsync(new MqttClientOptionsBuilder()
            .WithClientId("cleanup-check").WithTcpServer("localhost", _brokerPort).Build(), ct);
        await subscriber.SubscribeAsync(new MqttTopicFilterBuilder()
            .WithTopic("bowire/probe/#").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct);

        // Retained messages arrive immediately on subscribe; give it a moment.
        var delivered = await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(1), ct)) == got.Task;
        Assert.False(delivered, "A retained message survived — the probe did not clean up after itself.");
    }

    private static int FindFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
