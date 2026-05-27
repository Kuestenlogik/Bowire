// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Pulsar;
using Xunit;

namespace Kuestenlogik.Bowire.IntegrationTests.Pulsar;

/// <summary>
/// End-to-end round-trip suite against a live Apache Pulsar broker
/// (via Testcontainers). Covers the DotPulsar-driven produce /
/// subscribe paths that the in-process HTTP-admin fake in
/// <see cref="PulsarPluginIntegrationTests"/> can't reach.
/// </summary>
[Trait("Category", "Docker")]
public sealed class PulsarRoundTripE2ETests : IClassFixture<PulsarContainerFixture>
{
    private readonly PulsarContainerFixture _broker;

    public PulsarRoundTripE2ETests(PulsarContainerFixture broker)
    {
        _broker = broker;
    }

    [Fact]
    public async Task Produce_Then_Subscribe_Round_Trips_The_Payload()
    {
        var plugin = new BowirePulsarProtocol();
        var topic = "persistent://public/default/bowire-rt-" + Guid.NewGuid().ToString("N")[..8];

        // Produce one message first; we'll subscribe from Earliest so
        // the cursor replays the backlog instead of waiting for new
        // traffic. This sidesteps the "subscribe ack races produce"
        // problem that Latest-position subscriptions face.
        var produced = await plugin.InvokeAsync(
            serverUrl: _broker.BrokerUrl,
            service: "ignored",
            method: "pulsar/topic/" + topic + "/produce",
            jsonMessages: ["hello from the round-trip suite"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", produced.Status);
        Assert.False(string.IsNullOrEmpty(produced.Metadata["message_id"]));
        Assert.Equal(topic, produced.Metadata["topic"]);

        using var streamCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            streamCts.Token, TestContext.Current.CancellationToken);

        string? first = null;
        await foreach (var msg in plugin.InvokeStreamAsync(
            serverUrl: _broker.BrokerUrl,
            service: "ignored",
            method: "pulsar/topic/" + topic + "/subscribe",
            jsonMessages: [],
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                ["subscription_name"] = "round-trip-" + Guid.NewGuid().ToString("N")[..6],
                ["from_latest"] = "false",
            },
            ct: linkedCts.Token))
        {
            first = msg;
            break;
        }

        Assert.NotNull(first);
        Assert.Contains("hello from the round-trip suite", first);
    }

    [Fact]
    public async Task InvokeAsync_With_Bad_Method_Returns_Routing_Error_Even_Against_Live_Broker()
    {
        var plugin = new BowirePulsarProtocol();
        var result = await plugin.InvokeAsync(
            serverUrl: _broker.BrokerUrl,
            service: "ignored",
            method: "garbage",
            jsonMessages: [""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Contains("Unknown Pulsar route", result.Status);
    }

    [Fact]
    public async Task Produce_Honours_Topic_Override_Metadata()
    {
        var plugin = new BowirePulsarProtocol();
        var overrideTopic = "persistent://public/default/bowire-rt-override-"
            + Guid.NewGuid().ToString("N")[..8];

        var result = await plugin.InvokeAsync(
            serverUrl: _broker.BrokerUrl,
            service: "ignored",
            // Discovery topic — gets overridden by the metadata entry.
            method: "pulsar/topic/persistent://public/default/discovery-topic/produce",
            jsonMessages: ["payload"],
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                ["topic"] = overrideTopic,
            },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal(overrideTopic, result.Metadata["topic"]);
    }
}
