// Combined Server-Sent Events sample for Bowire. One project, both stories:
//
//   * Embedded — the workbench is mounted at /bowire and the bundled
//     sse-catalogue.json seeds the Sources rail with this host's streams.
//     Both endpoints are *discovered* rather than typed in as URLs: /events
//     carries the [SseEndpoint] attribute, /events/report is additionally
//     registered with AddBowireSseEndpoint — the two mechanisms from
//     docs/protocols/sse.md.
//   * Separate — it is a real SSE endpoint, so point an external workbench
//     or `bowire --url sse@http://localhost:5186/events` at it.
//
// The stream exercises the whole text/event-stream grammar the subscriber
// parses: a monotonic `id:` on every frame, `retry:` once on connect, a `:`
// comment as keep-alive every few seconds, and — on /events/report — a
// `data:` payload spanning several lines, which the parser joins back
// together. A bounded replay buffer honours the Last-Event-ID request
// header, so a client that drops off resumes where it left off:
//
//   curl -N http://localhost:5186/events
//   curl -N -H "Last-Event-ID: 12" http://localhost:5186/events
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.Sse
//   → open http://localhost:5186/bowire

using System.Globalization;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Protocol.Sse;
using Kuestenlogik.Bowire.Sources;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5186");

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

// The ticker runs on its own, not per connection — that is what gives a
// reconnecting client a real gap to resume across.
builder.Services.AddSingleton<TickFeed>();
builder.Services.AddHostedService<TickPump>();

var app = builder.Build();

// Discovery mechanism 1 — the [SseEndpoint] attribute on the handler. The
// plugin scans the endpoint metadata, so the stream shows up in the rail
// without anyone pasting a URL.
app.MapGet("/events", [SseEndpoint(Description = "Ticker — one tick per second, resumable via Last-Event-ID.", EventType = "tick")]
    (HttpContext ctx, TickFeed feed) => StreamAsync(ctx, feed, "tick", multiLine: false));

// Discovery mechanism 2 — fluent registration. Manual registrations take
// precedence over the scan, so this is the name and description the rail
// shows for the endpoint below.
app.AddBowireSseEndpoint(
    "/events/report",
    "Status report",
    "The same ticks, pretty-printed across several data: lines.",
    "report");

app.MapGet("/events/report", [SseEndpoint(Description = "Status report — data: spans multiple lines.", EventType = "report")]
    (HttpContext ctx, TickFeed feed) => StreamAsync(ctx, feed, "report", multiLine: true));

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();

// Streams the shared feed to one client: replay from the resume point, then
// live. `Since` covers both, so there is a single code path for either.
static async Task StreamAsync(HttpContext ctx, TickFeed feed, string eventType, bool multiLine)
{
    const int reconnectMs = 3000;
    var keepAliveEvery = TimeSpan.FromSeconds(5);
    var poll = TimeSpan.FromMilliseconds(250);

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    var ct = ctx.RequestAborted;

    // The SSE spec sends the last id a client saw back as the Last-Event-ID
    // request header on reconnect — replay from there instead of from now.
    // A fresh client (no header, or garbage) joins live at the end of the
    // buffer rather than getting the whole window dumped on it.
    var resumed = long.TryParse(
        ctx.Request.Headers["Last-Event-ID"].ToString(),
        NumberStyles.Integer, CultureInfo.InvariantCulture, out var resume);
    var lastSeq = resumed ? resume : feed.LastSeq;

    try
    {
        // retry: tells the client how long to wait before reconnecting —
        // sent once, up front, as its own frame.
        await WriteFrameAsync(ctx.Response, $"retry: {reconnectMs}", ct);
        await WriteFrameAsync(
            ctx.Response,
            resumed ? $": resuming after id {lastSeq}" : $": connected at id {lastSeq}",
            ct);

        var lastKeepAlive = DateTimeOffset.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            foreach (var tick in feed.Since(lastSeq))
            {
                var data = multiLine
                    ? $$"""
                        {
                          "seq": {{tick.Seq}},
                          "at": "{{tick.At:O}}",
                          "note": "this payload spans several data: lines"
                        }
                        """
                    : $$"""{"seq":{{tick.Seq}},"at":"{{tick.At:O}}"}""";

                // A payload with newlines becomes one data: field per line;
                // the subscriber joins them back with "\n".
                var body = "data: " + data.Replace("\n", "\ndata: ", StringComparison.Ordinal);
                await WriteFrameAsync(ctx.Response, $"id: {tick.Seq}\nevent: {eventType}\n{body}", ct);
                lastSeq = tick.Seq;
            }

            if (DateTimeOffset.UtcNow - lastKeepAlive >= keepAliveEvery)
            {
                // ':' comment — a no-op for the parser, but it keeps idle
                // proxies from reaping the connection.
                await WriteFrameAsync(ctx.Response, $": keep-alive {DateTimeOffset.UtcNow:O}", ct);
                lastKeepAlive = DateTimeOffset.UtcNow;
            }

            await Task.Delay(poll, ct);
        }
    }
    catch (OperationCanceledException)
    {
        // Client disconnected — expected.
    }
}

// One frame = the fields, then the blank line that dispatches the event.
static async Task WriteFrameAsync(HttpResponse response, string frame, CancellationToken ct)
{
    await response.WriteAsync(frame + "\n\n", ct);
    await response.Body.FlushAsync(ct);
}

/// <summary>A single tick, numbered by the monotonic sequence that becomes its SSE id.</summary>
sealed record Tick(long Seq, DateTimeOffset At);

/// <summary>
/// The shared ticker: a bounded ring buffer with a monotonic sequence. The
/// bound is what makes Last-Event-ID resume demonstrable — a client that
/// reconnects with the last id it saw gets everything since, up to the
/// buffer window (roughly four minutes here).
/// </summary>
sealed class TickFeed
{
    private const int Capacity = 256;

    private readonly Lock _gate = new();
    private readonly Queue<Tick> _buffer = new();
    private long _seq;

    /// <summary>The newest id handed out — where a client without Last-Event-ID joins.</summary>
    public long LastSeq
    {
        get { lock (_gate) return _seq; }
    }

    public void Emit()
    {
        lock (_gate)
        {
            _buffer.Enqueue(new Tick(++_seq, DateTimeOffset.UtcNow));
            while (_buffer.Count > Capacity) _buffer.Dequeue();
        }
    }

    /// <summary>Buffered ticks newer than <paramref name="lastSeq"/> — the replay window.</summary>
    public IReadOnlyList<Tick> Since(long lastSeq)
    {
        lock (_gate) return [.. _buffer.Where(t => t.Seq > lastSeq)];
    }
}

/// <summary>Drives the feed once a second, whether or not anyone is subscribed.</summary>
sealed class TickPump(TickFeed feed) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                feed.Emit();
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
    }
}
