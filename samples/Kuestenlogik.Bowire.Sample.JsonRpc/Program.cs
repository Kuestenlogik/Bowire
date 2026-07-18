// Combined JSON-RPC 2.0 sample for Bowire. One project, both stories:
//
//   * Embedded — the workbench is mounted at /bowire and the bundled
//     jsonrpc-catalogue.json seeds the Sources rail with this host's /rpc
//     endpoint. rpc.discover (OpenRPC) lets the plugin auto-list methods.
//   * Separate — it is a real JSON-RPC server, so point an external
//     workbench or `bowire --url jsonrpc@http://localhost:5187/rpc` at it.
//
// Methods: add / subtract / divide, plus rpc.discover (OpenRPC).
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.JsonRpc
//   → open http://localhost:5187/bowire

using System.Text.Json;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sources;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5187");

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();

app.MapPost("/rpc", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var raw = await reader.ReadToEndAsync();
    var req = JsonNode.Parse(raw);
    if (req is null) { ctx.Response.StatusCode = 400; return; }

    var id = req["id"];
    var method = req["method"]?.GetValue<string>() ?? "";
    var p = req["params"];

    object? result = null;
    object? error = null;
    try
    {
        result = method switch
        {
            "rpc.discover" => BuildOpenRpc(),
            "add" => Two(p) is (var a, var b) ? a + b : throw new ArgumentException("params"),
            "subtract" => Two(p) is (var a, var b) ? a - b : throw new ArgumentException("params"),
            "divide" => Two(p) is (var a, var b)
                ? (b == 0 ? throw new InvalidOperationException("div by zero") : (object)((double)a / b))
                : throw new ArgumentException("params"),
            _ => throw new InvalidOperationException("method not found"),
        };
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
    {
        // Sample JSON-RPC dispatcher: the switch arms throw exactly
        // these three; anything else escapes to ASP.NET's default handler.
        error = new { code = -32601, message = ex.Message };
        result = null;
    }

    var envelope = error is null
        ? (object)new { jsonrpc = "2.0", id = id?.ToJsonString(), result }
        : new { jsonrpc = "2.0", id = id?.ToJsonString(), error };

    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync(JsonSerializer.Serialize(envelope));
});

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();

static (int a, int b)? Two(JsonNode? p)
{
    if (p is JsonArray arr && arr.Count >= 2)
        return (arr[0]!.GetValue<int>(), arr[1]!.GetValue<int>());
    if (p is JsonObject obj && obj["a"] is { } a && obj["b"] is { } b)
        return (a.GetValue<int>(), b.GetValue<int>());
    return null;
}

static object BuildOpenRpc() => new
{
    openrpc = "1.2.6",
    info = new { title = "Bowire Math Sample", version = "1.0.0" },
    methods = new object[]
    {
        new {
            name = "add", summary = "Returns a + b.",
            @params = new object[] {
                new { name = "a", required = true, schema = new { type = "integer" } },
                new { name = "b", required = true, schema = new { type = "integer" } },
            },
            result = new { name = "sum", schema = new { type = "integer" } },
        },
        new {
            name = "subtract", summary = "Returns a - b.",
            @params = new object[] {
                new { name = "a", required = true, schema = new { type = "integer" } },
                new { name = "b", required = true, schema = new { type = "integer" } },
            },
            result = new { name = "difference", schema = new { type = "integer" } },
        },
        new {
            name = "divide", summary = "Returns a / b as a double.",
            @params = new object[] {
                new { name = "a", required = true, schema = new { type = "integer" } },
                new { name = "b", required = true, schema = new { type = "integer" } },
            },
            result = new { name = "quotient", schema = new { type = "number" } },
        },
    },
};
