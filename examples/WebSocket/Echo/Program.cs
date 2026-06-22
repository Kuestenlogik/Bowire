// WebSocket echo sample for the Bowire WebSocket plugin demo. Every
// inbound text frame is echoed back prefixed with "echo: " so the
// workbench can see round-trip behaviour without spinning up a real
// app.

using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5185");
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

await app.RunAsync();
