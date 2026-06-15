// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.Nats;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Testcontainers.Nats;
using Xunit;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Testcontainers-backed round-trip coverage for the NATS plugin's
/// async-iterator state machines. These methods produce huge IL when
/// the compiler lowers them (one branch per <c>await</c>) and the IL
/// stays cold without a real broker, which is why the package-level
/// coverage report sits at ~49%. The existing
/// <c>Nats/NatsRoundTripE2ETests</c> suite runs against a
/// subprocess-launched <c>nats-server</c> binary for Docker-free CI;
/// this file runs the same methods against a Testcontainers-managed
/// <c>nats:2-alpine</c> broker so the Docker dev lane covers the
/// state machines too.
/// </summary>
/// <remarks>
/// <para>
/// Specifically targets:
/// <list type="bullet">
///   <item><c>NatsJetStreamDiscovery.ListAsync</c> — async-iterator
///     fan-out over <c>js.ListStreamsAsync</c> with per-stream method
///     synthesis (info / consume / publish-per-filter).</item>
///   <item><c>NatsServicesDiscovery.ListAsync</c> — PING-broadcast
///     collect + INFO-per-name fan-out + endpoint enumeration.</item>
///   <item><c>NatsDiscovery.ScanSubjectsAsync</c> — wildcard
///     subscribe-window subject sampler.</item>
///   <item>The plugin's <c>InvokeStreamAsync</c> "consume" route —
///     the StreamJetStreamConsumerAsync state machine — drained for
///     N messages with monotonic sequence assertion.</item>
/// </list>
/// </para>
/// <para>
/// All tests carry <c>[Trait("Category","Docker")]</c> so the default
/// CI lane (<c>--filter "Category!=Docker"</c>) skips them.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class NatsJetStreamRoundTripTests : IAsyncLifetime
{
    // Default Testcontainers.Nats image already enables -js (JetStream)
    // via the builder's Init flags. We re-pin so a future
    // Testcontainers.Nats default bump doesn't silently swap us onto
    // a NATS line whose JetStream wire is incompatible. Container
    // construction is deferred to InitializeAsync so a Docker-less
    // host that runs the constructor (e.g. xUnit reflecting test
    // discovery before the trait filter kicks in) doesn't trip over
    // the Docker-endpoint check the builder runs in Build().
    private NatsContainer? _container;
    private string _serverUrl = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _container = new NatsBuilder("nats:2.10-alpine").Build();
        using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _container.StartAsync(startCts.Token);
        _serverUrl = _container.GetConnectionString();
    }

    public ValueTask DisposeAsync() =>
        _container is null ? ValueTask.CompletedTask : _container.DisposeAsync();

    [Fact]
    public async Task DiscoverAsync_Lists_JetStream_Streams_With_Info_Consume_And_PublishPerFilter_Methods()
    {
        var ct = TestContext.Current.CancellationToken;
        // Two streams with two different subject filter layouts so
        // ListAsync's async-iterator fan-out has more than one tuple
        // to walk + so the per-filter method synthesis doesn't get
        // away with a single-stream / single-filter shortcut.
        await CreateStreamAsync("ORDERS", ["orders.created", "orders.shipped"], ct);
        await CreateStreamAsync("AUDIT", ["audit.>"], ct);

        var plugin = new BowireNatsProtocol();
        var services = await plugin.DiscoverAsync(
            _serverUrl, showInternalServices: false, ct: ct);

        var orders = Assert.Single(services, s => s.Name == "JetStream:ORDERS");
        var audit = Assert.Single(services, s => s.Name == "JetStream:AUDIT");

        // ORDERS has two subject filters → info + consume + two
        // publish-into-stream methods = 4 total.
        Assert.Equal(4, orders.Methods.Count);
        Assert.Contains(orders.Methods, m => m.Name == "info");
        Assert.Contains(orders.Methods, m => m.Name == "consume");
        Assert.Contains(orders.Methods,
            m => m.FullName == "nats/jetstream/ORDERS/publish/orders.created");
        Assert.Contains(orders.Methods,
            m => m.FullName == "nats/jetstream/ORDERS/publish/orders.shipped");

        // AUDIT has one subject filter → info + consume + one publish = 3.
        Assert.Equal(3, audit.Methods.Count);
        Assert.Contains(audit.Methods,
            m => m.FullName == "nats/jetstream/AUDIT/publish/audit.>");
    }

    [Fact]
    public async Task JetStream_Publish_Then_Consume_Drains_N_Messages_In_Monotonic_Order()
    {
        var ct = TestContext.Current.CancellationToken;
        const int count = 4;
        await CreateStreamAsync("TELEMETRY", ["telemetry.cpu"], ct);

        var plugin = new BowireNatsProtocol();

        // Publish via the plugin's InvokeAsync JS-acked route. Each
        // ack response is JSON with stream+seq+duplicate fields.
        for (var i = 0; i < count; i++)
        {
            var ack = await plugin.InvokeAsync(
                _serverUrl,
                service: "JetStream:TELEMETRY",
                method: "nats/jetstream/TELEMETRY/publish/telemetry.cpu",
                jsonMessages: [$$"""{"sample":{{i}}}"""],
                showInternalServices: false,
                metadata: null,
                ct: ct);
            Assert.Equal("OK", ack.Status);
            Assert.NotNull(ack.Response);
            using var ackDoc = JsonDocument.Parse(ack.Response!);
            Assert.Equal("TELEMETRY", ackDoc.RootElement.GetProperty("stream").GetString());
        }

        // Drain via InvokeStreamAsync — the StreamJetStreamConsumerAsync
        // state machine. Cancel after N to break the otherwise-
        // unbounded iterator.
        using var consumeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var received = new List<string>();
        try
        {
            await foreach (var envelope in plugin.InvokeStreamAsync(
                _serverUrl,
                service: "JetStream:TELEMETRY",
                method: "nats/jetstream/TELEMETRY/consume",
                jsonMessages: ["{}"],
                showInternalServices: false,
                metadata: null,
                ct: consumeCts.Token))
            {
                received.Add(envelope);
                if (received.Count >= count)
                {
                    await consumeCts.CancelAsync();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected — broke out via cancellation.
        }

        Assert.Equal(count, received.Count);

        // Each envelope is JSON { subject, seq, data, ... }. Sequences
        // must climb 1..N in production order.
        var seqs = received
            .Select(env => JsonDocument.Parse(env).RootElement.GetProperty("seq").GetUInt64())
            .ToList();
        Assert.Equal(Enumerable.Range(1, count).Select(i => (ulong)i).ToArray(), seqs);
    }

    [Fact]
    public async Task JetStream_Info_Returns_StreamName_And_State_Fields_As_Json()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateStreamAsync("EVENTS", ["events.>"], ct);

        var plugin = new BowireNatsProtocol();
        var result = await plugin.InvokeAsync(
            _serverUrl,
            service: "JetStream:EVENTS",
            method: "nats/jetstream/EVENTS/info",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: ct);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        using var doc = JsonDocument.Parse(result.Response!);
        Assert.Equal("EVENTS", doc.RootElement.GetProperty("name").GetString());
        // State block — the `messages` field is always present even on
        // a freshly created stream (value 0).
        Assert.True(doc.RootElement.TryGetProperty("messages", out _),
            "info JSON must expose the stream's state.messages field");
    }

    [Fact]
    public async Task Discover_Picks_Up_Live_NatsService_Name_And_Endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        // Stand a Services-API service up on the broker; the plugin's
        // NatsServicesDiscovery.ListAsync should walk $SRV.PING +
        // $SRV.INFO and pick it up under the "Service:<name>" prefix.
        await using var svcConn = new NatsConnection(NatsOpts.Default with { Url = _serverUrl });
        await svcConn.ConnectAsync();
        var svcs = new NATS.Client.Services.NatsSvcContext(svcConn);
        await using var service = await svcs.AddServiceAsync(
            "calc", "1.0.0", queueGroup: "calcq", cancellationToken: ct);
        await service.AddEndpointAsync<byte[]>(
            name: "add",
            subject: "calc.add",
            handler: async msg =>
            {
                await msg.ReplyAsync<byte[]>(msg.Data ?? [], cancellationToken: ct);
            },
            cancellationToken: ct);

        var plugin = new BowireNatsProtocol();
        var services = await plugin.DiscoverAsync(
            _serverUrl, showInternalServices: false, ct: ct);

        var calc = Assert.Single(services, s => s.Name == "Service:calc");
        Assert.Contains(calc.Methods, m => m.FullName == "nats/services/calc/add");
    }

    [Fact]
    public async Task Discover_Subject_Sampler_Buckets_Active_Traffic_By_First_Token()
    {
        var ct = TestContext.Current.CancellationToken;
        // The wildcard-subscribe subject sampler
        // (NatsDiscovery.ScanSubjectsAsync) only sees subjects that
        // carry traffic during the scan window. Pump in two distinct
        // first-tokens so we can assert both buckets appear.
        await using var publisher = new NatsConnection(NatsOpts.Default with { Url = _serverUrl });
        await publisher.ConnectAsync();

        using var pubCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                while (!pubCts.Token.IsCancellationRequested)
                {
                    await publisher.PublishAsync<byte[]>(
                        "metrics.cpu", Encoding.UTF8.GetBytes("{}"),
                        cancellationToken: pubCts.Token);
                    await publisher.PublishAsync<byte[]>(
                        "metrics.mem", Encoding.UTF8.GetBytes("{}"),
                        cancellationToken: pubCts.Token);
                    await publisher.PublishAsync<byte[]>(
                        "logs.app", Encoding.UTF8.GetBytes("{}"),
                        cancellationToken: pubCts.Token);
                    await Task.Delay(100, pubCts.Token);
                }
            }
            catch { /* expected on cancel */ }
        }, pubCts.Token);

        var plugin = new BowireNatsProtocol();
        var services = await plugin.DiscoverAsync(
            _serverUrl, showInternalServices: false, ct: ct);

        await pubCts.CancelAsync();
        try { await pumpTask; } catch { /* swallow */ }

        // Both first-tokens must appear as separate Bowire services.
        Assert.Contains(services, s => s.Name == "metrics");
        Assert.Contains(services, s => s.Name == "logs");
    }

    // ----- helpers -----------------------------------------------------

    private async Task CreateStreamAsync(string name, string[] subjectFilters, CancellationToken ct)
    {
        await using var conn = new NatsConnection(NatsOpts.Default with { Url = _serverUrl });
        await conn.ConnectAsync();
        var js = conn.CreateJetStreamContext();
        await js.CreateStreamAsync(
            new StreamConfig(name, subjectFilters),
            cancellationToken: ct);
    }
}
