// Combined Server-Sent Events sample for Bowire. One project, both stories:
//
//   * Embedded — the workbench is mounted at /bowire and the bundled
//     sse-catalogue.json seeds the Sources rail with this host's /events
//     stream.
//   * Separate — it is a real SSE endpoint, so point an external workbench
//     or `bowire --url sse@http://localhost:5186/events` at it.
//
// Streams one "tick" event per second with a wall-clock ISO-8601 payload
// until the client disconnects.
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.Sse
//   → open http://localhost:5186/bowire

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sources;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5186");

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();

app.MapGet("/events", async (HttpContext ctx) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    var i = 0;
    while (!ctx.RequestAborted.IsCancellationRequested)
    {
        var payload = $"{{\"seq\":{++i},\"at\":\"{DateTime.UtcNow:O}\"}}";
        await ctx.Response.WriteAsync($"event: tick\ndata: {payload}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        try { await Task.Delay(TimeSpan.FromSeconds(1), ctx.RequestAborted); }
        catch (OperationCanceledException) { break; }
    }
});

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();
