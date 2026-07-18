// Combined WebSocket sample for Bowire. One project, both stories:
//
//   * Embedded — the workbench is mounted at /bowire and the bundled
//     websocket-catalogue.json seeds the Sources rail with this host's
//     /ws echo endpoint.
//   * Separate — it is a real WebSocket server, so point an external
//     workbench or `bowire --url websocket@ws://localhost:5185/ws` at it.
//
// Every inbound text frame is echoed back prefixed with "echo: ".
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.WebSocket
//   → open http://localhost:5185/bowire

using System.Net.WebSockets;
using System.Text;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sources;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5185");

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();
app.UseWebSockets();

app.MapGet("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("WebSocket upgrade required.");
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
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
});

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();
