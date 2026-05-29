// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.Nats;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;

namespace Kuestenlogik.Bowire.IntegrationTests.Nats;

/// <summary>
/// End-to-end round-trip checks for the NATS plugin against a real
/// <c>nats-server -js</c> container. Phase-1 unit tests cover the
/// helpers; this file covers the wire — actual publish/subscribe,
/// request/reply, JetStream stream listing + publish + ordered-
/// consumer streaming, NATS Services API discovery, and the
/// queue-group subscription hint.
/// </summary>
/// <remarks>
/// <para>
/// Marked <c>[Trait("Category", "Docker")]</c> so CI / dev runs
/// without a Docker daemon opt out via
/// <c>dotnet test --filter "Category!=Docker"</c>. The Bowire CI
/// matrix runs both passes.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class NatsRoundTripE2ETests : IClassFixture<NatsContainerFixture>
{
    private readonly NatsContainerFixture _broker;

    public NatsRoundTripE2ETests(NatsContainerFixture broker)
    {
        _broker = broker;
    }

    // ----- Phase 1: core pub/sub + req/reply ----------------------------

    [Fact]
    public async Task Publish_Then_Stream_Subscribe_Round_Trips_The_Payload()
    {
        var ct = TestContext.Current.CancellationToken;
        var plugin = new BowireNatsProtocol();
        const string subject = "demo.echo";

        // Start the subscribe stream first, then publish so the
        // message lands while we're actively reading. Run them on
        // separate tasks so the stream's awaiting foreach doesn't
        // block the publish.
        var firstMessage = StreamOneAsync(plugin, _broker.ServerUrl, subject, ct);

        // Tiny wait to let the SubscribeAsync above register on the
        // broker — NATS subscribe is fire-and-forget, the SUB frame
        // takes a millisecond or two to flush. Without this the
        // publish can race ahead and the subscriber misses the msg.
        await Task.Delay(200, ct);

        var publishResult = await plugin.InvokeAsync(
            _broker.ServerUrl,
            service: "(root)",
            method: $"nats/{subject}/publish",
            jsonMessages: ["""{"hello":"world"}"""],
            showInternalServices: false,
            metadata: null,
            ct: ct);

        Assert.Equal("OK", publishResult.Status);

        var envelope = await firstMessage.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Contains(subject, envelope, StringComparison.Ordinal);
        Assert.Contains("hello", envelope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_Returns_Replier_Payload_With_Subject_Metadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var plugin = new BowireNatsProtocol();
        const string subject = "demo.add";

        // Stand up a tiny replier on the same broker via the raw
        // SDK; the plugin's RequestAsync should round-trip through
        // it. The replier completes on its own once the request
        // arrives, so no manual teardown needed beyond ct.
        await using var repliers = new NatsConnection(NatsOpts.Default with { Url = _broker.ServerUrl });
        await repliers.ConnectAsync();

        var replierTask = Task.Run(async () =>
        {
            await foreach (var msg in repliers.SubscribeAsync<byte[]>(subject, cancellationToken: ct))
            {
                if (msg.ReplyTo is null) continue;
                await repliers.PublishAsync<byte[]>(
                    msg.ReplyTo,
                    Encoding.UTF8.GetBytes("\"sum: 42\""),
                    cancellationToken: ct);
                return;
            }
        }, ct);

        await Task.Delay(200, ct);

        var result = await plugin.InvokeAsync(
            _broker.ServerUrl,
            service: "(root)",
            method: $"nats/{subject}/request",
            jsonMessages: ["""{"a":1,"b":41}"""],
            showInternalServices: false,
            metadata: null,
            ct: ct);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("sum: 42", result.Response!, StringComparison.Ordinal);
        // RequestAsync writes 'subject' into the metadata so
        // downstream loggers / UI can show what was hit.
        Assert.True(result.Metadata.ContainsKey("subject"));

        // Wait for the replier loop to finish so its cancellation
        // doesn't leak past the test.
        try { await replierTask.WaitAsync(TimeSpan.FromSeconds(5), ct); }
        catch { /* fall-through — the test already asserted */ }
    }

    [Fact]
    public async Task Discover_Picks_Up_Subjects_That_Were_Active_During_Scan()
    {
        // Discovery's subject-scanner relies on traffic flowing past
        // during the scan window. Publish a couple of messages in
        // the background while DiscoverAsync runs and assert the
        // resulting service tree picks them up under the right
        // service prefix.
        var ct = TestContext.Current.CancellationToken;
        var plugin = new BowireNatsProtocol();

        await using var publisher = new NatsConnection(NatsOpts.Default with { Url = _broker.ServerUrl });
        await publisher.ConnectAsync();

        using var pubCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumpTask = Task.Run(async () =>
        {
            // Pump every 100 ms for the full scan window plus a
            // little headroom — guarantees at least a handful of
            // messages fall inside the scan no matter how the
            // container start latency lines up.
            try
            {
                while (!pubCts.Token.IsCancellationRequested)
                {
                    await publisher.PublishAsync<byte[]>(
                        "audit.created", Encoding.UTF8.GetBytes("{}"),
                        cancellationToken: pubCts.Token);
                    await publisher.PublishAsync<byte[]>(
                        "audit.updated", Encoding.UTF8.GetBytes("{}"),
                        cancellationToken: pubCts.Token);
                    await Task.Delay(100, pubCts.Token);
                }
            }
            catch { /* expected on ct cancellation */ }
        }, pubCts.Token);

        var services = await plugin.DiscoverAsync(
            _broker.ServerUrl,
            showInternalServices: false,
            ct: ct);

        await pubCts.CancelAsync();
        try { await pumpTask; } catch { /* swallow */ }

        // The Phase-1 subject sampler buckets by first dot-token.
        Assert.Contains(services, s => s.Name == "audit");
    }

    // ----- Phase 2: JetStream meta + publish + consume ------------------

    [Fact]
    public async Task JetStream_Stream_Surfaces_In_Discovery_With_Info_Consume_And_Publish()
    {
        var ct = TestContext.Current.CancellationToken;
        // Pre-create a JetStream stream via the raw SDK so the
        // plugin's discovery has something to find. The discovery
        // call returns one BowireServiceInfo per stream.
        await CreateStreamAsync("ORDERS", "orders.>", ct);

        var plugin = new BowireNatsProtocol();
        var services = await plugin.DiscoverAsync(
            _broker.ServerUrl,
            showInternalServices: false,
            ct: ct);

        var ordersStream = Assert.Single(services, s => s.Name == "JetStream:ORDERS");
        // info + consume + one publish-into-stream method per
        // filtered subject. The subject 'orders.>' counts as a single
        // filter on the config so we expect three methods total.
        Assert.Equal(3, ordersStream.Methods.Count);
        Assert.Contains(ordersStream.Methods, m => m.Name == "info");
        Assert.Contains(ordersStream.Methods, m => m.Name == "consume");
        Assert.Contains(ordersStream.Methods, m => m.FullName == "nats/jetstream/ORDERS/publish/orders.>");
    }

    [Fact]
    public async Task JetStream_Info_Returns_Config_And_State_As_Json()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateStreamAsync("EVENTS", "events.>", ct);

        var plugin = new BowireNatsProtocol();
        var result = await plugin.InvokeAsync(
            _broker.ServerUrl,
            service: "JetStream:EVENTS",
            method: "nats/jetstream/EVENTS/info",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: ct);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        var doc = JsonDocument.Parse(result.Response!);
        Assert.Equal("EVENTS", doc.RootElement.GetProperty("name").GetString());
        // Brand-new stream → zero messages but the property is there.
        Assert.True(doc.RootElement.TryGetProperty("messages", out _));
    }

    [Fact]
    public async Task JetStream_Publish_Then_Consume_Round_Trips_Through_Ordered_Consumer()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateStreamAsync("METRICS", "metrics.cpu", ct);

        var plugin = new BowireNatsProtocol();

        // Publish three messages into the stream via the JS-acked
        // path. Each one should come back from the consume stream
        // in order with a sequence number.
        for (var i = 0; i < 3; i++)
        {
            var ack = await plugin.InvokeAsync(
                _broker.ServerUrl,
                service: "JetStream:METRICS",
                method: "nats/jetstream/METRICS/publish/metrics.cpu",
                jsonMessages: [$$"""{"sample":{{i}}}"""],
                showInternalServices: false,
                metadata: null,
                ct: ct);
            Assert.Equal("OK", ack.Status);
            Assert.NotNull(ack.Response);
            // PubAck JSON has stream + seq + duplicate fields.
            var ackDoc = JsonDocument.Parse(ack.Response!);
            Assert.Equal("METRICS", ackDoc.RootElement.GetProperty("stream").GetString());
        }

        // Consume — finite because we cancel after three messages.
        using var consumeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var received = new List<string>();
        try
        {
            await foreach (var envelope in plugin.InvokeStreamAsync(
                _broker.ServerUrl,
                service: "JetStream:METRICS",
                method: "nats/jetstream/METRICS/consume",
                jsonMessages: ["{}"],
                showInternalServices: false,
                metadata: null,
                ct: consumeCts.Token))
            {
                received.Add(envelope);
                if (received.Count >= 3)
                {
                    await consumeCts.CancelAsync();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected — we broke out via cancellation.
        }

        Assert.Equal(3, received.Count);
        // Sequence numbers must climb monotonically.
        var seqs = received
            .Select(env => JsonDocument.Parse(env).RootElement.GetProperty("seq").GetUInt64())
            .ToList();
        Assert.Equal(new ulong[] { 1, 2, 3 }, seqs);
    }

    // ----- Phase 2: queue groups --------------------------------------

    [Fact]
    public async Task Queue_Group_Subscribers_Share_Messages_Instead_Of_Broadcasting()
    {
        var ct = TestContext.Current.CancellationToken;
        var plugin = new BowireNatsProtocol();
        const string subject = "queue.demo";
        const string queueGroup = "workers";

        // Two subscribers join the same queue group via the plugin.
        // NATS distributes each incoming message to exactly one of
        // them — the test asserts the combined count equals the
        // publish count (would be 2× without the queue group).
        var meta = new Dictionary<string, string> { ["queue_group"] = queueGroup };

        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var counterA = 0;
        var counterB = 0;
        async Task RunSubscriberAsync(int which)
        {
            try
            {
                await foreach (var _ in plugin.InvokeStreamAsync(
                    _broker.ServerUrl,
                    service: "(root)",
                    method: $"nats/{subject}/subscribe",
                    jsonMessages: ["{}"],
                    showInternalServices: false,
                    metadata: meta,
                    ct: stopCts.Token))
                {
                    if (which == 0) Interlocked.Increment(ref counterA);
                    else Interlocked.Increment(ref counterB);
                }
            }
            catch { /* swallow on cancel */ }
        }

        var subA = Task.Run(() => RunSubscriberAsync(0), stopCts.Token);
        var subB = Task.Run(() => RunSubscriberAsync(1), stopCts.Token);

        // Readiness probe — a fixed Task.Delay is racy: core NATS has no
        // persistence, so any message published before both SUB frames
        // are registered server-side is silently dropped (that's how a
        // slow runner ends up with "Actual: 1"). Instead, publish probe
        // messages until one is observed, which proves the subscriptions
        // are live; then reset the counters for the measured batch.
        var warmupDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (Volatile.Read(ref counterA) + Volatile.Read(ref counterB) == 0
            && DateTime.UtcNow < warmupDeadline)
        {
            await plugin.InvokeAsync(
                _broker.ServerUrl,
                service: "(root)",
                method: $"nats/{subject}/publish",
                jsonMessages: ["""{"probe":true}"""],
                showInternalServices: false,
                metadata: null,
                ct: ct);
            await Task.Delay(100, ct);
        }
        Assert.True(Volatile.Read(ref counterA) + Volatile.Read(ref counterB) > 0,
            "queue-group subscriptions never went live within the warm-up window");

        // Let any in-flight probe deliveries settle, then zero the
        // counters so the measured batch is clean.
        await Task.Delay(300, ct);
        Interlocked.Exchange(ref counterA, 0);
        Interlocked.Exchange(ref counterB, 0);

        const int publishCount = 20;
        for (var i = 0; i < publishCount; i++)
        {
            await plugin.InvokeAsync(
                _broker.ServerUrl,
                service: "(root)",
                method: $"nats/{subject}/publish",
                jsonMessages: [$$"""{"i":{{i}}}"""],
                showInternalServices: false,
                metadata: null,
                ct: ct);
        }

        // Let the queue group drain.
        await Task.Delay(800, ct);
        await stopCts.CancelAsync();
        try { await Task.WhenAll(subA, subB).WaitAsync(TimeSpan.FromSeconds(3), ct); }
        catch { /* swallow */ }

        var total = counterA + counterB;
        Assert.Equal(publishCount, total);
        // Both subscribers should have seen something. Distribution
        // isn't strictly balanced (NATS picks per-message), but with
        // 20 messages both > 0 is reliable.
        Assert.True(counterA > 0, $"subscriber A received {counterA} messages");
        Assert.True(counterB > 0, $"subscriber B received {counterB} messages");
    }

    // ----- Phase 2: NATS Services API -----------------------------------

    [Fact]
    public async Task Services_Discovery_Picks_Up_A_Live_NatsService_With_Endpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        // Stand up a NATS Services-API service on the broker and ask
        // the plugin to discover it. The plugin's
        // NatsServicesDiscovery.ListAsync round-trips $SRV.PING +
        // $SRV.INFO and should return one BowireServiceInfo per
        // advertised service plus one method per endpoint.
        await using var svcConn = new NatsConnection(NatsOpts.Default with { Url = _broker.ServerUrl });
        await svcConn.ConnectAsync();
        var svcs = new NATS.Client.Services.NatsSvcContext(svcConn);
        await using var service = await svcs.AddServiceAsync(
            "echo", "1.0.0", queueGroup: "echoq", cancellationToken: ct);
        await service.AddEndpointAsync<byte[]>(
            name: "say",
            subject: "echo.say",
            handler: async msg =>
            {
                await msg.ReplyAsync<byte[]>(msg.Data ?? [], cancellationToken: ct);
            },
            cancellationToken: ct);

        // The Services API stays running for the lifetime of svcs;
        // discovery should see it during the next pass.
        var plugin = new BowireNatsProtocol();
        var services = await plugin.DiscoverAsync(
            _broker.ServerUrl,
            showInternalServices: false,
            ct: ct);

        var echoService = Assert.Single(services, s => s.Name == "Service:echo");
        Assert.Contains(echoService.Methods,
            m => m.FullName == "nats/services/echo/say");
    }

    // ----- helpers -----------------------------------------------------

    private async Task CreateStreamAsync(string name, string subjectFilter, CancellationToken ct)
    {
        await using var conn = new NatsConnection(NatsOpts.Default with { Url = _broker.ServerUrl });
        await conn.ConnectAsync();
        var js = conn.CreateJetStreamContext();
        await js.CreateStreamAsync(
            new StreamConfig(name, [subjectFilter]),
            cancellationToken: ct);
    }

    private static async Task<string> StreamOneAsync(
        BowireNatsProtocol plugin, string serverUrl, string subject, CancellationToken ct)
    {
        await foreach (var envelope in plugin.InvokeStreamAsync(
            serverUrl,
            service: "(root)",
            method: $"nats/{subject}/subscribe",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: ct))
        {
            return envelope;
        }
        throw new InvalidOperationException("subscribe yielded no messages before cancellation");
    }
}
