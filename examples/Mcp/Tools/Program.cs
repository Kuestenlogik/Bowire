// MCP sample for the Bowire MCP plugin demo. Hosts two tools (echo +
// add) over the official Model Context Protocol SDK so the workbench
// can discover and invoke them via the streamable-HTTP transport.

using System.ComponentModel;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5190");
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<SampleTools>();

var app = builder.Build();
app.MapMcp("/mcp");
await app.RunAsync();

[McpServerToolType]
public sealed class SampleTools
{
    [McpServerTool, Description("Echo the input text back to the caller.")]
    public string Echo(string text) => "echo: " + text;

    [McpServerTool, Description("Add two integers.")]
    public int Add(int a, int b) => a + b;
}
