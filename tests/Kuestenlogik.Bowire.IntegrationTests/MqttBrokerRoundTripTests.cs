// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Kuestenlogik.Bowire.Protocol.Mqtt;
using Xunit;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Testcontainers-backed round-trip coverage for the MQTT plugin's
/// <c>InvokeAsync</c> publish path, <c>InvokeStreamAsync</c> subscribe
/// loop, and the broker-side ack envelope (topic + payload + QoS +
/// retain metadata round-trip). The plugin's existing in-process
/// suite under <c>Kuestenlogik.Bowire.Mock.Tests</c> exercises the
/// embedded broker baked into <c>MqttMockTransportHost</c>; this file
/// drives an Eclipse Mosquitto container so the MQTTnet client's
/// real-TCP path runs end-to-end against the same broker
/// implementation production users would hit.
/// </summary>
/// <remarks>
/// <para>
/// Testcontainers has no first-party MQTT module — we use the generic
/// <c>ContainerBuilder</c> with <c>eclipse-mosquitto:2</c> and a
/// minimal in-container config that turns off auth (allow-anonymous)
/// + binds the standard 1883 listener. The standard 1883 port is
/// mapped to an ephemeral host port so parallel CI lanes don't
/// collide.
/// </para>
/// <para>
/// All tests carry <c>[Trait("Category","Docker")]</c> so the default
/// CI lane (<c>--filter "Category!=Docker"</c>) skips them.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class MqttBrokerRoundTripTests : IAsyncLifetime
{
    private const ushort MqttPort = 1883;

    // Inline Mosquitto config: a single listener on 1883 + auth off
    // (the workbench is talking to the broker as a test harness, not
    // a production gateway). Persistence is also off so the
    // container's filesystem stays clean for the next test run.
    private const string MosquittoConf = """
        listener 1883
        allow_anonymous true
        persistence false
        """;

    // Container construction is deferred to InitializeAsync so a
    // Docker-less host that runs the constructor (e.g. xUnit reflecting
    // test discovery before the trait filter kicks in) doesn't trip
    // over the Docker-endpoint check the builder runs in Build().
    private IContainer? _container;
    private string _brokerUrl = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _container = new ContainerBuilder("eclipse-mosquitto:2")
            .WithPortBinding(MqttPort, assignRandomHostPort: true)
            // The official image expects /mosquitto/config/mosquitto.conf
            // by default; ship one with auth off so the plugin can
            // connect anonymously like it does against a workbench-
            // local broker.
            .WithResourceMapping(
                System.Text.Encoding.UTF8.GetBytes(MosquittoConf),
                "/mosquitto/config/mosquitto.conf")
            // Wait for the broker to log "mosquitto version 2.x
            // starting" — the closest log line to "ready for
            // connections" Mosquitto emits on a clean boot.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("mosquitto version"))
            .Build();

        using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _container.StartAsync(startCts.Token);

        // ParseBrokerUrl tolerates both mqtt:// and bare host:port
        // forms — we use mqtt:// so the test assertion can reuse the
        // exact same string a workbench user would paste.
        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(MqttPort);
        _brokerUrl = $"mqtt://{host}:{port}";
    }

    public ValueTask DisposeAsync() =>
        _container is null ? ValueTask.CompletedTask : _container.DisposeAsync();

    [Fact]
    public async Task InvokeAsync_Publishes_To_Broker_And_Returns_Topic_QoS_Retain_Envelope()
    {
        var plugin = new BowireMqttProtocol();
        var topic = "bowire/test/" + Guid.NewGuid().ToString("N")[..8];
        const string payload = """{"sensor":"temp","value":21.5}""";

        var result = await plugin.InvokeAsync(
            serverUrl: _brokerUrl,
            service: "ignored",
            method: topic,
            jsonMessages: [payload],
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                ["qos"] = "AtLeastOnce",
                ["retain"] = "true",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);

        // The post-publish envelope round-trips the broker-side ack
        // shape: { topic, payload, qos, retain }. Parse + assert
        // structurally rather than substring-matching so a shape
        // regression fails meaningfully.
        using var doc = JsonDocument.Parse(result.Response!);
        Assert.Equal(topic, doc.RootElement.GetProperty("topic").GetString());
        Assert.Equal(payload, doc.RootElement.GetProperty("payload").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("qos").GetInt32());
        Assert.True(doc.RootElement.GetProperty("retain").GetBoolean());

        // Metadata mirror — UI surfaces these without re-parsing JSON.
        Assert.Equal(topic, result.Metadata["topic"]);
        Assert.Equal("1", result.Metadata["qos"]);
        Assert.Equal("true", result.Metadata["retain"]);
    }

    [Fact]
    public async Task InvokeStreamAsync_Delivers_Each_Published_Message_As_Json_Envelope()
    {
        var plugin = new BowireMqttProtocol();
        var topic = "bowire/round-trip/" + Guid.NewGuid().ToString("N")[..8];
        var payloads = new[] { "first", "second", "third" };

        using var streamCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            streamCts.Token, TestContext.Current.CancellationToken);

        // Spin the subscriber up first so the broker has the SUB
        // registered before the publishes land. MQTT 3.1.1 has no
        // session replay for QoS0 + clean-session=true, so a
        // produce-before-subscribe would lose them.
        var received = new List<string>();
        var receivedEnough = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var consumerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var envelope in plugin.InvokeStreamAsync(
                    serverUrl: _brokerUrl,
                    service: "ignored",
                    method: topic,
                    jsonMessages: [],
                    showInternalServices: false,
                    metadata: new Dictionary<string, string> { ["qos"] = "AtLeastOnce" },
                    ct: linkedCts.Token))
                {
                    lock (received) received.Add(envelope);
                    if (received.Count >= payloads.Length)
                    {
                        receivedEnough.TrySetResult(true);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on test wind-down.
            }
        }, linkedCts.Token);

        // Give the SUB a moment to register. A fixed 500 ms is enough
        // against a single-host Mosquitto container; if it ever
        // flakes we'd switch to a probe-driven readiness loop.
        await Task.Delay(500, linkedCts.Token);

        // Publish via the plugin's InvokeAsync — this also exercises
        // the per-publish connect/disconnect lifecycle MQTTnet runs.
        foreach (var p in payloads)
        {
            var ack = await plugin.InvokeAsync(
                serverUrl: _brokerUrl,
                service: "ignored",
                method: topic,
                jsonMessages: [p],
                showInternalServices: false,
                metadata: new Dictionary<string, string> { ["qos"] = "AtLeastOnce" },
                ct: linkedCts.Token);
            Assert.Equal("OK", ack.Status);
        }

        // Wait for all N to arrive — bounded so a broker hiccup fails
        // rather than hanging the suite.
        var completed = await Task.WhenAny(receivedEnough.Task, Task.Delay(15_000, linkedCts.Token));
        Assert.Same(receivedEnough.Task, completed);

        await streamCts.CancelAsync();
        try { await consumerTask; } catch { /* swallow */ }

        List<string> snapshot;
        lock (received) snapshot = [.. received];

        Assert.Equal(payloads.Length, snapshot.Count);

        // Each envelope: { topic, qos, retain, payload, bytes }.
        var payloadsBack = snapshot
            .Select(env => JsonDocument.Parse(env).RootElement.GetProperty("payload").GetString())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        var expected = payloads.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, payloadsBack);

        Assert.All(snapshot, env =>
        {
            using var d = JsonDocument.Parse(env);
            Assert.Equal(topic, d.RootElement.GetProperty("topic").GetString());
            Assert.Equal(1, d.RootElement.GetProperty("qos").GetInt32());
            // bytes field must reflect the UTF-8 byte count of the
            // payload — sanity-check it's non-zero (the exact value
            // varies per payload, so the per-element payload check
            // above already pins the data).
            Assert.True(d.RootElement.GetProperty("bytes").GetInt32() > 0);
        });
    }

    [Fact]
    public async Task Retain_Flag_Survives_Across_A_Fresh_Subscription()
    {
        // The retain-bit branch in InvokeAsync's MqttApplicationMessage
        // builder is only meaningful end-to-end if the broker holds
        // the message and replays it to a fresh subscriber. Publish
        // retained, then subscribe with a *new* client and assert we
        // receive the same payload back.
        var plugin = new BowireMqttProtocol();
        var topic = "bowire/retained/" + Guid.NewGuid().ToString("N")[..8];
        const string payload = "remember-me";

        var publish = await plugin.InvokeAsync(
            serverUrl: _brokerUrl,
            service: "ignored",
            method: topic,
            jsonMessages: [payload],
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                ["qos"] = "AtLeastOnce",
                ["retain"] = "true",
            },
            ct: TestContext.Current.CancellationToken);
        Assert.Equal("OK", publish.Status);

        // Subscribe after the publish. Because the message was
        // retained, the broker should deliver it as soon as the SUB
        // lands.
        using var streamCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            streamCts.Token, TestContext.Current.CancellationToken);

        string? envelope = null;
        await foreach (var msg in plugin.InvokeStreamAsync(
            serverUrl: _brokerUrl,
            service: "ignored",
            method: topic,
            jsonMessages: [],
            showInternalServices: false,
            metadata: null,
            ct: linkedCts.Token))
        {
            envelope = msg;
            break;
        }

        Assert.NotNull(envelope);
        using var doc = JsonDocument.Parse(envelope!);
        Assert.Equal(topic, doc.RootElement.GetProperty("topic").GetString());
        Assert.Equal(payload, doc.RootElement.GetProperty("payload").GetString());
        // Retained replays carry the retain flag set on the wire.
        Assert.True(doc.RootElement.GetProperty("retain").GetBoolean());
    }
}
