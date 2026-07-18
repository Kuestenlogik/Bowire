// Combined MCP sample for Bowire. One project, both stories:
//
//   * Embedded — the workbench is mounted at /bowire and the bundled
//     mcp-catalogue.json seeds the Sources rail with this host's /mcp
//     endpoint, discovered over the streamable-HTTP transport.
//   * Separate — it is a real MCP server, so point an external workbench
//     or `bowire --url mcp@http://localhost:5190/mcp` at it.
//
// Two tools: echo + add.
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.Mcp
//   → open http://localhost:5190/bowire

using System.ComponentModel;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sources;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5190");

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<SampleTools>();

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();
app.MapMcp("/mcp");

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();

[McpServerToolType]
internal sealed class SampleTools
{
    [McpServerTool, Description("Echo the input text back to the caller.")]
    public static string Echo(string text) => "echo: " + text;

    [McpServerTool, Description("Add two integers.")]
    public static int Add(int a, int b) => a + b;
}
