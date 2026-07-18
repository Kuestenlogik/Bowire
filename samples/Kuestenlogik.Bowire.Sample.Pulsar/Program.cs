// Combined Pulsar sample for Bowire. Pulsar has no .NET-embeddable
// broker, so this sample points at an *external* one (docker-compose.yml
// alongside) while still telling both stories from one project:
//
//   * Embedded — the workbench is mounted at /bowire and the bundled
//     pulsar-catalogue.json seeds the Sources rail with the broker; a
//     background producer publishes one message per second to
//     persistent://public/default/bowire-sample so the topic has traffic.
//   * Separate — point an external workbench or
//     `bowire --url pulsar://localhost:6650` at the same broker.
//
// The producer is resilient: if the broker isn't up yet (no
// `docker compose up`), the host + workbench still start and it keeps
// retrying until the broker appears.
//
// Run:
//   docker compose up                                     # start the broker
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.Pulsar
//   → open http://localhost:5194/bowire

using DotPulsar;
using DotPulsar.Extensions;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sources;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5194");

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();

// ---- Resilient producer: one message per second to bowire-sample ----
_ = Task.Run(async () =>
{
    var ct = app.Lifetime.ApplicationStopping;
    await using var client = PulsarClient.Builder()
        .ServiceUrl(new Uri("pulsar://localhost:6650"))
        .Build();
    await using var producer = client.NewProducer(Schema.String)
        .Topic("persistent://public/default/bowire-sample")
        .Create();

    var i = 0;
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await producer.Send($"hello from bowire sample #{++i} @ {DateTime.UtcNow:O}", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Broker not up yet (no `docker compose up`) — keep the host
            // and workbench alive; the topic lights up once it appears.
            app.Logger.LogDebug(ex, "Pulsar publish failed (broker down?) — retrying");
        }
        try { await Task.Delay(1000, ct); }
        catch (OperationCanceledException) { break; }
    }
});

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();
