// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.Pulsar;
using Testcontainers.Pulsar;
using Xunit;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Additional Testcontainers-backed round-trip coverage for the
/// Pulsar plugin's <c>InvokeAsync</c> publish path and the
/// <c>InvokeStreamAsync</c> consume loop. The existing
/// <c>Pulsar/PulsarRoundTripE2ETests</c> suite covers the
/// "single-message produce then subscribe one" smoke path; this
/// file targets the still-uncovered branches:
/// the post-<c>Send</c> metadata envelope (JSON shape with topic,
/// message_id and byte count), the backlog-replay path on
/// <c>Earliest</c> with multiple messages, and discovery against
/// a live broker (<c>BuildPubSubModeAsync</c>-class HTTP admin
/// surface returning a real topic list rather than the in-process
/// fake).
/// </summary>
/// <remarks>
/// <para>
/// This class owns its own short-lived <see cref="PulsarContainer"/>
/// rather than using the <c>Pulsar/PulsarContainerFixture</c> from the
/// sibling suite — the user explicitly requested a top-level
/// <c>PulsarBrokerRoundTripTests.cs</c> file, so the fixture is
/// per-class and disposes in <see cref="DisposeAsync"/>. xUnit gives
/// each test its own instance, but <see cref="IAsyncLifetime"/>'s
/// initialise/dispose runs only once per class instantiation — fine
/// for a small N.
/// </para>
/// <para>
/// All tests carry <c>[Trait("Category","Docker")]</c> so the default
/// CI lane (<c>--filter "Category!=Docker"</c>) skips them; only the
/// Docker-equipped dev box / nightly lane runs the broker round-trip.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class PulsarBrokerRoundTripTests : IAsyncLifetime
{
    // Pin the broker image to the same Testcontainers.Pulsar-compatible
    // 3.3.x line the sibling Pulsar/PulsarContainerFixture uses, so
    // pulls hit the same cache layer on a dev box that ran both
    // suites. Container construction is deferred to InitializeAsync
    // so a Docker-less host that runs the constructor (e.g. xUnit
    // reflecting test discovery before the trait filter kicks in)
    // doesn't trip over the Docker-endpoint check the builder runs
    // in Build().
    private PulsarContainer? _container;
    private string _brokerUrl = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _container = new PulsarBuilder("apachepulsar/pulsar:3.3.0").Build();
        // 60s container-start budget — long enough for a cold pull on a
        // slow runner, short enough to fail fast if Docker is broken.
        using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _container.StartAsync(startCts.Token);
        _brokerUrl = _container.GetBrokerAddress();
    }

    public ValueTask DisposeAsync() =>
        _container is null ? ValueTask.CompletedTask : _container.DisposeAsync();

    [Fact]
    public async Task InvokeAsync_Returns_PostSend_Envelope_With_Topic_MessageId_And_Byte_Count()
    {
        using var plugin = new BowirePulsarProtocol();
        var topic = "persistent://public/default/bowire-envelope-"
            + Guid.NewGuid().ToString("N")[..8];
        const string payload = "hello-envelope";

        var result = await plugin.InvokeAsync(
            serverUrl: _brokerUrl,
            service: "ignored",
            method: "pulsar/topic/" + topic + "/produce",
            jsonMessages: [payload],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);

        // The post-Send branch in InvokeAsync serializes
        // { topic, message_id, bytes } back to the caller. Parse +
        // assert against each field rather than substring-matching
        // so a field rename / shape regression actually fails.
        using var doc = JsonDocument.Parse(result.Response!);
        Assert.Equal(topic, doc.RootElement.GetProperty("topic").GetString());

        var msgId = doc.RootElement.GetProperty("message_id").GetString();
        Assert.False(string.IsNullOrEmpty(msgId), "message_id must be non-empty");

        // 14 ASCII chars in "hello-envelope" → 14 bytes UTF-8.
        Assert.Equal(payload.Length, doc.RootElement.GetProperty("bytes").GetInt32());

        // Metadata mirror: topic + message_id must round-trip into the
        // dictionary so downstream loggers can use them without
        // re-parsing the response.
        Assert.Equal(topic, result.Metadata["topic"]);
        Assert.Equal(msgId, result.Metadata["message_id"]);
    }

    [Fact]
    public async Task InvokeStreamAsync_Replays_Backlog_From_Earliest_In_Production_Order()
    {
        using var plugin = new BowirePulsarProtocol();
        var topic = "persistent://public/default/bowire-backlog-"
            + Guid.NewGuid().ToString("N")[..8];

        // Produce three messages before any subscriber exists. From
        // Latest these'd be lost; from Earliest the cursor replays
        // them — which is the only path that exercises the
        // consume-loop's "drain pre-existing backlog" branch.
        var payloads = new[] { "first", "second", "third" };
        foreach (var p in payloads)
        {
            var ack = await plugin.InvokeAsync(
                serverUrl: _brokerUrl,
                service: "ignored",
                method: "pulsar/topic/" + topic + "/produce",
                jsonMessages: [p],
                showInternalServices: false,
                metadata: null,
                ct: TestContext.Current.CancellationToken);
            Assert.Equal("OK", ack.Status);
        }

        // Now subscribe with from_latest=false (Earliest position) and
        // collect exactly N messages. The consume loop also has to
        // acknowledge each one for the cursor to advance, so the
        // counter / break combo also exercises the post-yield Ack
        // branch.
        using var streamCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            streamCts.Token, TestContext.Current.CancellationToken);

        var received = new List<string>();
        await foreach (var envelope in plugin.InvokeStreamAsync(
            serverUrl: _brokerUrl,
            service: "ignored",
            method: "pulsar/topic/" + topic + "/subscribe",
            jsonMessages: [],
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                ["subscription_name"] = "backlog-" + Guid.NewGuid().ToString("N")[..6],
                ["from_latest"] = "false",
            },
            ct: linkedCts.Token))
        {
            received.Add(envelope);
            if (received.Count >= payloads.Length) break;
        }

        Assert.Equal(payloads.Length, received.Count);

        // Each envelope is JSON { topic, message_id, payload,
        // publish_time }. Pull the payloads back out and check they
        // arrived in the order they were produced — Pulsar's
        // exclusive subscription guarantees per-topic ordering.
        var payloadsBack = received
            .Select(e => JsonDocument.Parse(e).RootElement.GetProperty("payload").GetString())
            .ToList();
        Assert.Equal(payloads, payloadsBack);

        // Every envelope must carry the same topic and a non-empty
        // message_id.
        Assert.All(received, env =>
        {
            using var d = JsonDocument.Parse(env);
            Assert.Equal(topic, d.RootElement.GetProperty("topic").GetString());
            Assert.False(string.IsNullOrEmpty(
                d.RootElement.GetProperty("message_id").GetString()));
        });
    }

    [Fact]
    public async Task DiscoverAsync_Hits_Live_Admin_Surface_And_Surfaces_Produced_Topic()
    {
        using var plugin = new BowirePulsarProtocol();
        // Use a deterministic leaf name so we can find it in the
        // service list without grep-by-prefix.
        var leaf = "bowire-discover-" + Guid.NewGuid().ToString("N")[..8];
        var topic = "persistent://public/default/" + leaf;

        // Produce one message so the broker registers the topic in
        // its persistent-topic catalogue — pulsar-standalone
        // auto-creates on produce, which is what the workbench
        // discovery would walk into.
        var ack = await plugin.InvokeAsync(
            serverUrl: _brokerUrl,
            service: "ignored",
            method: "pulsar/topic/" + topic + "/produce",
            jsonMessages: ["x"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal("OK", ack.Status);

        // DiscoverAsync hits the HTTP admin surface
        // (/admin/v2/persistent/public/default) — the same code path
        // the in-process fake covers, but here against a real broker
        // so PulsarConnectionHelper.Resolve, the HttpClient timeout
        // path, and the JSON parser all run end-to-end.
        //
        // The Pulsar admin REST API is eventually consistent: produce
        // returns OK before the broker has registered the new topic
        // in its persistent-topic catalogue, so a discovery fired
        // immediately after the produce intermittently returns an
        // empty list. Poll for up to 15 s; in green-path runs the
        // topic shows up within one or two iterations.
        List<Kuestenlogik.Bowire.Models.BowireServiceInfo> services = [];
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            services = await plugin.DiscoverAsync(
                serverUrl: _brokerUrl,
                showInternalServices: false,
                ct: TestContext.Current.CancellationToken);
            if (services.Any(s => s.Name == leaf)) break;
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        Assert.NotEmpty(services);
        var produced = Assert.Single(services, s => s.Name == leaf);
        Assert.Equal("pulsar", produced.Source);
        Assert.Equal(2, produced.Methods.Count);
        Assert.Contains(produced.Methods, m => m.FullName.EndsWith("/produce", StringComparison.Ordinal));
        Assert.Contains(produced.Methods, m => m.FullName.EndsWith("/subscribe", StringComparison.Ordinal));
    }
}
