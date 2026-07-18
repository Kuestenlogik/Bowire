// Combined SignalR sample for Bowire. One project, both stories:
//
//   * Embedded — the workbench is mounted at /bowire and the bundled
//     signalr-catalogue.json seeds the Sources rail with this host's
//     /chathub hub.
//   * Separate — it is a real SignalR host, so point an external workbench
//     or `bowire --url signalr@http://localhost:5184/chathub` at it.
//
// One hub (ChatHub): SendMessage broadcasts to everyone, Echo round-trips.
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.SignalR
//   → open http://localhost:5184/bowire

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sources;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5184");
builder.Services.AddSignalR();

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();
app.MapHub<ChatHub>("/chathub");

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();

sealed class ChatHub : Hub
{
    public Task SendMessage(string user, string message)
        => Clients.All.SendAsync("ReceiveMessage", user, message);

    public string Echo(string text) => "echo: " + text;
}
