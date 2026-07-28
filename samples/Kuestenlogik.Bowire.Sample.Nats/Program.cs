// Combined NATS sample for Bowire. NATS has no .NET-embeddable server, so
// this sample points at an *external* broker (docker-compose.yml
// alongside) while still telling both stories from one project:
//
//   * Embedded — the workbench is mounted at /bowire and the bundled
//     nats-catalogue.json seeds the Sources rail with the broker. A
//     background workload then keeps all three of the plugin's discovery
//     sources lit instead of only the first one:
//       - subject sampling — heartbeats on two prefixes (`bowire.*` and
//         `telemetry.*`) so the sidebar shows prefix grouping, plus a
//         responder on `bowire.>` so the discovered *Request* method
//         answers instead of timing out;
//       - JetStream — a TELEMETRY stream created at startup over the
//         `telemetry.*` subjects, so the JetStream:TELEMETRY tree has
//         info/consume/publish with real stored messages behind them;
//       - Services API — an `echo` service advertised over `$SRV.PING`
//         with one `svc.echo.say` endpoint, so Service:echo appears.
//   * Separate — point an external workbench or
//     `bowire --url nats://localhost:4222` at the same broker.
//
// The whole workload is resilient: if the broker isn't up yet (no
// `docker compose up`), the host + workbench still start and every piece
// keeps retrying until the broker appears.
//
// Run:
//   docker compose up                                    # start the broker
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.Nats
//   → open http://localhost:5193/bowire

using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sources;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5193");

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);
// Publisher, responder, JetStream seeding and the Services-API
// registration share one connection and one deterministic lifecycle, so
// they run as a single hosted service.
builder.Services.AddHostedService<NatsSampleWorkload>();

var app = builder.Build();
app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();

// Keeps the external broker interesting enough that every NATS discovery
// source has something to report. Everything below is best-effort: a
// broker that is down (or goes away mid-run) only produces Debug log
// lines, and each piece re-establishes itself once it comes back.
sealed class NatsSampleWorkload(ILogger<NatsSampleWorkload> logger) : BackgroundService
{
    private const string Url = "nats://localhost:4222";

    // Prefix #1 — plain-text heartbeat plus a req/reply subject.
    private const string SampleSubject = "bowire.sample";
    private const string EchoSubject = "bowire.echo";
    // One wildcard subscription covers every `bowire.*` subject, so the
    // Request method the plugin discovers returns on all of them — not
    // just on the dedicated echo subject.
    private const string ResponderFilter = "bowire.>";

    // Prefix #2 — JSON readings, also the JetStream stream's subjects.
    private const string CpuSubject = "telemetry.cpu";
    private const string MemorySubject = "telemetry.memory";
    private const string StreamName = "TELEMETRY";

    // Services API — advertised over `$SRV.PING`, discovered as Service:echo.
    private const string ServiceName = "echo";
    private const string ServiceEndpointSubject = "svc.echo.say";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // NatsClient doesn't dial on construction and reconnects on its
        // own, so building it before the broker exists is safe.
        await using var nats = new NatsClient(Url);

        // Long-lived workers; both own their retry loop.
        var responder = RunResponderAsync(nats, stoppingToken);
        var serviceApi = RunServiceApiAsync(nats, stoppingToken);

        await RunPublisherAsync(nats, stoppingToken);

        // The two workers swallow their own failures; awaiting them keeps
        // the shared client alive until they have wound down.
        try { await Task.WhenAll(responder, serviceApi); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    // ---- Publisher: two subject prefixes + the JetStream stream --------
    private async Task RunPublisherAsync(NatsClient nats, CancellationToken ct)
    {
        var js = nats.CreateJetStreamContext();
        var streamReady = false;
        var i = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Created lazily and re-attempted after any failure: with
                // the broker down this throws, so the JetStream tree fills
                // in as soon as `docker compose up` lands. CreateStreamAsync
                // is idempotent for an unchanged config.
                if (!streamReady)
                {
                    await js.CreateStreamAsync(
                        // Explicit subjects rather than a `telemetry.>`
                        // wildcard: the plugin surfaces one publish method
                        // per filtered subject, and a wildcard filter would
                        // surface a method nobody can publish to.
                        new StreamConfig(StreamName, [CpuSubject, MemorySubject])
                        {
                            Description = "Bowire NATS sample telemetry",
                            // The sample publishes forever — keep the
                            // stream bounded so it can't grow without end.
                            MaxMsgs = 1_000,
                        },
                        cancellationToken: ct);
                    streamReady = true;
                    logger.LogInformation(
                        "JetStream stream {Stream} ready over {Cpu} + {Memory}",
                        StreamName, CpuSubject, MemorySubject);
                }

                i++;

                // Prefix #1 — the original plain-text heartbeat, plus one
                // on the echo subject so the wildcard scan discovers it
                // (a scan only ever sees subjects that move).
                await nats.PublishAsync(
                    SampleSubject,
                    $"hello from bowire sample #{i} @ {DateTime.UtcNow:O}",
                    cancellationToken: ct);
                await nats.PublishAsync(
                    EchoSubject,
                    $"heartbeat #{i} — send a Request here for a reply",
                    cancellationToken: ct);

                // Prefix #2 — core publishes, not JetStream ones. The
                // stream captures them all the same (that's what a subject
                // filter does), while a JetStream publish would round-trip
                // its PubAck through an `_INBOX.*` reply subject that the
                // plugin's wildcard scan would then list as a service.
                await nats.PublishAsync(
                    CpuSubject,
                    JsonSerializer.Serialize(new
                    {
                        seq = i,
                        loadPercent = Math.Round(20.0 + (i % 40) * 1.5, 1),
                        at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }),
                    cancellationToken: ct);
                await nats.PublishAsync(
                    MemorySubject,
                    JsonSerializer.Serialize(new
                    {
                        seq = i,
                        usedMb = 512 + (i % 64) * 8,
                        at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }),
                    cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Broker not up yet (no `docker compose up`) — keep the
                // host and workbench alive; the subjects and the stream
                // light up once it appears.
                streamReady = false;
                logger.LogDebug(ex, "NATS publish failed (broker down?) — retrying");
            }

            try { await Task.Delay(Tick, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ---- Responder: makes the discovered Request method return ---------
    private async Task RunResponderAsync(NatsClient nats, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var msg in nats.SubscribeAsync<string>(
                    ResponderFilter, cancellationToken: ct))
                {
                    // Our own heartbeats carry no reply subject; only real
                    // requests — e.g. the workbench's Request method — get
                    // an answer.
                    if (string.IsNullOrEmpty(msg.ReplyTo)) continue;

                    await msg.ReplyAsync(
                        $"echo from the Bowire NATS sample: {msg.Data} " +
                        $"(subject {msg.Subject}, {DateTime.UtcNow:O})",
                        cancellationToken: ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "NATS responder dropped (broker down?) — resubscribing");
            }

            try { await Task.Delay(Tick, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ---- Services API: the third discovery source ----------------------
    private async Task RunServiceApiAsync(NatsClient nats, CancellationToken ct)
    {
        var svcs = nats.CreateServicesContext();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // The same echo behaviour as the raw responder above, but
                // advertised the NATS-native way: the plugin's `$SRV.PING`
                // sweep finds it, then `$SRV.INFO.echo` enumerates the
                // endpoints below into methods.
                await using var service = await svcs.AddServiceAsync(
                    ServiceName, "1.0.0", queueGroup: "bowire-sample", cancellationToken: ct);
                await service.AddEndpointAsync<string>(
                    // Name and subject deliberately identical: the plugin
                    // addresses a discovered Services endpoint by its
                    // *name* (`nats/services/echo/<name>`), so an endpoint
                    // whose name and subject differ would discover fine and
                    // then answer "No responders" when invoked.
                    name: ServiceEndpointSubject,
                    subject: ServiceEndpointSubject,
                    handler: async msg => await msg.ReplyAsync(
                        $"say: {msg.Data} ({DateTime.UtcNow:O})", cancellationToken: ct),
                    cancellationToken: ct);

                logger.LogInformation(
                    "NATS service {Service} advertised with endpoint {Subject}",
                    ServiceName, ServiceEndpointSubject);

                // Stay registered until shutdown — the service answers
                // `$SRV.*` on its own from here.
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { /* shutting down */ }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "NATS service registration failed (broker down?) — retrying");
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(Tick, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
