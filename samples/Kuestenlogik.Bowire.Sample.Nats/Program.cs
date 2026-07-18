// Combined NATS sample for Bowire. NATS has no .NET-embeddable server, so
// this sample points at an *external* broker (docker-compose.yml
// alongside) while still telling both stories from one project:
//
//   * Embedded — the workbench is mounted at /bowire and the bundled
//     nats-catalogue.json seeds the Sources rail with the broker; a
//     background publisher emits one message per second on `bowire.sample`
//     so the subject has live traffic.
//   * Separate — point an external workbench or
//     `bowire --url nats://localhost:4222` at the same broker.
//
// The publisher is resilient: if the broker isn't up yet (no
// `docker compose up`), the host + workbench still start and the
// publisher keeps retrying until the broker appears.
//
// Run:
//   docker compose up                                    # start the broker
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.Nats
//   → open http://localhost:5193/bowire

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sources;
using NATS.Net;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5193");

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();

// ---- Resilient publisher: one message per second on bowire.sample ----
_ = Task.Run(async () =>
{
    var ct = app.Lifetime.ApplicationStopping;
    await using var nats = new NatsClient("nats://localhost:4222");
    var i = 0;
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await nats.PublishAsync(
                "bowire.sample",
                $"hello from bowire sample #{++i} @ {DateTime.UtcNow:O}",
                cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Broker not up yet (no `docker compose up`) — keep the host
            // and workbench alive; the subject lights up once it appears.
            app.Logger.LogDebug(ex, "NATS publish failed (broker down?) — retrying");
        }
        try { await Task.Delay(1000, ct); }
        catch (OperationCanceledException) { break; }
    }
});

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();
