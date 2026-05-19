// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Protocol.Mqtt;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// Real-broker integration test for the AsyncAPI → MQTT routing path.
/// Mirrors the Phase A end-to-end flow that the Bowire.Samples.Mqtt +
/// Bowire.Samples.AsyncApi pair demonstrates, but in-process: an
/// embedded MQTTnet broker stands in for Mosquitto, an in-test
/// subscriber stands in for the workbench UI. No Testcontainers, no
/// Docker — keeps the test runnable on any CI without extra setup.
///
/// Covers the full chain: discover an AsyncAPI document that declares
/// `bindings.mqtt.qos: 2` + `retain: true` → InvokeAsync on the send
/// operation → MqttBindingResolver looks up the MQTT plugin via the
/// registry → plugin publishes to the broker with the qos/retain the
/// document specified. Subscriber asserts the message arrived with
/// the right topic + payload + flags.
/// </summary>
public sealed class MqttBindingResolverIntegrationTests : IAsyncLifetime
{
    private MqttServer? _broker;
    private int _brokerPort;

    public async ValueTask InitializeAsync()
    {
        // Pick a free TCP port the OS handed us so parallel test runs
        // (or a leftover broker from a crashed previous run) don't
        // collide on 1883. Bind a temporary listener just long
        // enough to learn the port; close it before handing the
        // number to MQTTnet so the broker can bind without
        // tripping on our placeholder.
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
    public async Task Send_operation_publishes_via_mqtt_plugin_with_doc_declared_qos()
    {
        var ct = TestContext.Current.CancellationToken;

        // ---------- Pre-arrange: subscriber ----------
        // Pin the QoS the broker should record on the inbound publish
        // — we'll assert it after the publish lands. ExactlyOnce
        // mirrors the doc's `qos: 2`.
        const string topic = "smarthome/light/measured";
        var received = new TaskCompletionSource<MqttApplicationMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var subscriberFactory = new MqttClientFactory();
        using var subscriber = subscriberFactory.CreateMqttClient();
        subscriber.ApplicationMessageReceivedAsync += async args =>
        {
            received.TrySetResult(args.ApplicationMessage);
            await Task.CompletedTask;
        };
        await subscriber.ConnectAsync(new MqttClientOptionsBuilder()
            .WithClientId("test-subscriber")
            .WithTcpServer("localhost", _brokerPort)
            .Build(), ct);
        await subscriber.SubscribeAsync(
            new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce)
                .Build(),
            ct);

        // ---------- Arrange: registry + AsyncAPI plugin ----------
        var registry = new BowireProtocolRegistry();
        registry.Register(new BowireMqttProtocol());

        var services = new ServiceCollection();
        services.AddSingleton(registry);
        using var sp = services.BuildServiceProvider();

        var asyncApi = new BowireAsyncApiProtocol();
        asyncApi.Initialize(sp);

        // ---------- Arrange: document pointing at our broker ----------
        // Inline so the broker port is reachable from the doc — the
        // test fixture's port is dynamic.
        var docPath = Path.Combine(
            Path.GetTempPath(),
            $"asyncapi-binding-it-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(docPath, $"""
            asyncapi: 3.0.0
            info:
              title: Binding Integration Test
              version: 1.0.0
            servers:
              broker:
                host: 'localhost:{_brokerPort}'
                protocol: mqtt
            channels:
              lightChannel:
                address: '{topic}'
            operations:
              sendMeasurement:
                action: send
                channel:
                  $ref: '#/channels/lightChannel'
                bindings:
                  mqtt:
                    qos: 2
                    retain: true
            """, ct);

        try
        {
            // ---------- Act: discover + invoke ----------
            var discovered = await asyncApi.DiscoverAsync(
                serverUrl: docPath, showInternalServices: false, ct: ct);
            Assert.Single(discovered);

            const string payload = """{"lumens":42}""";
            var result = await asyncApi.InvokeAsync(
                serverUrl: docPath,
                service: "lightChannel",
                method: "sendMeasurement",
                jsonMessages: [payload],
                showInternalServices: false,
                ct: ct);

            // ---------- Assert: invocation succeeded ----------
            Assert.Equal("OK", result.Status);
            Assert.Equal(topic, result.Metadata["topic"]);
            Assert.Equal("2", result.Metadata["qos"]);   // ExactlyOnce
            Assert.Equal("true", result.Metadata["retain"]);

            // ---------- Assert: broker delivered the message ----------
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            var deliveredTask = received.Task.WaitAsync(timeoutCts.Token);
            var message = await deliveredTask;

            Assert.Equal(topic, message.Topic);
            Assert.Equal(payload, Encoding.UTF8.GetString(message.Payload));
            Assert.Equal(MqttQualityOfServiceLevel.ExactlyOnce, message.QualityOfServiceLevel);
            // Note on Retain: incoming MQTT messages only carry the
            // retain flag when delivered as a *retained* message to
            // a fresh subscriber (replay), not on the live delivery
            // to an already-connected subscriber. The publish side
            // sent retain=true (verified via result.Metadata above),
            // which is the right end of the wire to assert from
            // here — the broker's retained-message store is its own
            // contract, tested by MQTTnet itself.
        }
        finally
        {
            File.Delete(docPath);
        }
    }

    private static int FindFreeTcpPort()
    {
        // The Bind+0 trick: ask the OS for an ephemeral port, read
        // back what it gave us, release immediately. Race-window
        // between release and broker re-bind is tiny; in practice
        // never seen a collision on CI runners.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
