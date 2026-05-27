// Server-Sent Events sample for the Bowire SSE plugin demo. Streams
// one "tick" event per second with a wall-clock ISO-8601 timestamp
// payload until the client disconnects.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5186");
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

await app.RunAsync();
