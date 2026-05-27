// SignalR chat sample for the Bowire SignalR plugin demo. One hub
// (ChatHub) with two methods (SendMessage broadcasts to everyone,
// Echo round-trips a string to the caller).

using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5184");
builder.Services.AddSignalR();
var app = builder.Build();

app.MapHub<ChatHub>("/chathub");
await app.RunAsync();

sealed class ChatHub : Hub
{
    public Task SendMessage(string user, string message)
        => Clients.All.SendAsync("ReceiveMessage", user, message);

    public string Echo(string text) => "echo: " + text;
}
