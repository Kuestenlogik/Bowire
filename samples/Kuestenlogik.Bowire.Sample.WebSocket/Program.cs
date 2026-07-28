// Combined WebSocket sample for Bowire. One project, both stories:
//
//   * Embedded — the workbench is mounted at /bowire and the bundled
//     websocket-catalogue.json seeds the Sources rail with this host's
//     endpoints. Every route below also carries [WebSocketEndpoint]
//     metadata, so embedded discovery lists them by name instead of
//     falling back to the ad-hoc "one method named after the URL path"
//     entry.
//   * Separate — it is a real WebSocket server, so point an external
//     workbench or `bowire --url websocket@ws://localhost:5185/ws` at it.
//
// Three endpoints, one per frame behaviour the Bowire channel surfaces:
//
//   /ws         Text echo. Every inbound text frame is echoed back
//               prefixed with "echo: ".
//   /ws/binary  Binary echo. Continuation frames are accumulated until
//               EndOfMessage and the whole payload is sent back as one
//               binary frame, so the channel shows the
//               { "type": "binary", "bytes": n, "base64": ... } envelope.
//               A text frame is a protocol error here and closes the
//               socket with 1003 InvalidMessageType.
//   /ws/json    Strict JSON. Text frames have to parse as JSON; anything
//               else closes with 1003 and a description, so the close
//               envelope shows a real status instead of the usual 1000.
//
// All three negotiate the "bowire-echo.v1" sub-protocol: a client that
// offers none at all still gets a plain connection (that is what the
// Bowire channel does by default), a client that does offer a list has to
// include bowire-echo.v1 or the upgrade is refused before it reaches 101.
// From the workbench, ask for it with the metadata header
// `X-Bowire-WebSocket-Subprotocol: bowire-echo.v1`.
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.WebSocket
//   → open http://localhost:5185/bowire

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Protocol.WebSocket;
using Kuestenlogik.Bowire.Sources;

// The one sub-protocol this server speaks.
const string SubProtocol = "bowire-echo.v1";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5185");

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();

// KeepAliveInterval turns on the ping/pong heartbeat that holds the
// connection open through idle proxies; KeepAliveTimeout makes it a real
// exchange — a peer that never answers a ping is aborted instead of
// lingering as a half-open socket.
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15),
    KeepAliveTimeout = TimeSpan.FromSeconds(10)
});

app.MapGet("/ws", async (HttpContext ctx) =>
{
    using var socket = await AcceptNegotiatedAsync(ctx);
    if (socket is null) return;

    var buf = new byte[8 * 1024];

    while (socket.State == WebSocketState.Open)
    {
        var result = await socket.ReceiveAsync(buf, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            break;
        }

        var text = Encoding.UTF8.GetString(buf, 0, result.Count);
        var reply = Encoding.UTF8.GetBytes("echo: " + text);
        await socket.SendAsync(reply, WebSocketMessageType.Text,
            endOfMessage: true, CancellationToken.None);
    }
})
.WithMetadata(new WebSocketEndpointAttribute(
    "Echo", "Text echo — every frame comes back prefixed with \"echo: \"."));

app.MapGet("/ws/binary", async (HttpContext ctx) =>
{
    using var socket = await AcceptNegotiatedAsync(ctx);
    if (socket is null) return;

    var buf = new byte[8 * 1024];
    using var message = new MemoryStream();

    while (socket.State == WebSocketState.Open)
    {
        message.SetLength(0);
        WebSocketReceiveResult result;

        // A message larger than the buffer arrives as several reads with
        // EndOfMessage=false — accumulate them before echoing, otherwise
        // the client gets the payload back chopped into pieces.
        do
        {
            result = await socket.ReceiveAsync(buf, CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                return;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                // 1003 is exactly this case: a frame type the endpoint
                // cannot accept. CloseOutputAsync, not CloseAsync — the
                // conversation is over, so there is no point waiting for
                // an acknowledgement a misbehaving client may never send.
                await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidMessageType,
                    "this endpoint accepts binary frames only", CancellationToken.None);
                return;
            }

            await message.WriteAsync(buf.AsMemory(0, result.Count), CancellationToken.None);
        }
        while (!result.EndOfMessage);

        await socket.SendAsync(message.ToArray(), WebSocketMessageType.Binary,
            endOfMessage: true, CancellationToken.None);
    }
})
.WithMetadata(new WebSocketEndpointAttribute(
    "Binary echo", "Echoes binary frames back verbatim; a text frame closes with 1003."));

app.MapGet("/ws/json", async (HttpContext ctx) =>
{
    using var socket = await AcceptNegotiatedAsync(ctx);
    if (socket is null) return;

    var buf = new byte[8 * 1024];

    while (socket.State == WebSocketState.Open)
    {
        var result = await socket.ReceiveAsync(buf, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            break;
        }

        var text = Encoding.UTF8.GetString(buf, 0, result.Count);
        try
        {
            using var doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            // The description rides along in the close frame — Bowire
            // renders both halves as { "type": "close", "status": 1003,
            // "description": ... }. Keep it short: a close reason may not
            // exceed 123 UTF-8 bytes.
            await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidMessageType,
                "expected a JSON text frame", CancellationToken.None);
            return;
        }

        // The payload already parsed, so splicing it in keeps the reply
        // valid JSON and the channel envelope shows it nested instead of
        // escaped.
        var reply = Encoding.UTF8.GetBytes("{\"ok\":true,\"received\":" + text + "}");
        await socket.SendAsync(reply, WebSocketMessageType.Text,
            endOfMessage: true, CancellationToken.None);
    }
})
.WithMetadata(new WebSocketEndpointAttribute(
    "Strict JSON", "Text frames must parse as JSON; anything else closes with 1003."));

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();

// Shared handshake for all three endpoints: refuse plain HTTP requests,
// then negotiate the sub-protocol. AcceptWebSocketAsync(protocol) writes
// the agreed token into the Sec-WebSocket-Protocol response header, which
// is what binds ClientWebSocket.SubProtocol on the caller's side.
async Task<WebSocket?> AcceptNegotiatedAsync(HttpContext ctx)
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("WebSocket upgrade required.");
        return null;
    }

    var requested = ctx.WebSockets.WebSocketRequestedProtocols;
    if (requested.Count == 0)
        return await ctx.WebSockets.AcceptWebSocketAsync();

    if (!requested.Contains(SubProtocol, StringComparer.Ordinal))
    {
        // Nothing in common — refuse the upgrade outright so the client
        // sees a handshake failure rather than a socket that dies a
        // moment after it opened.
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("Unsupported sub-protocol; this server speaks " + SubProtocol + ".");
        return null;
    }

    return await ctx.WebSockets.AcceptWebSocketAsync(SubProtocol);
}
