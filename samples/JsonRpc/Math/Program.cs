// JSON-RPC 2.0 sample for the Bowire JSON-RPC plugin demo. Exposes
// rpc.discover (OpenRPC) so the plugin can auto-list available methods
// without a freeform fallback, plus add / subtract / divide.

using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5187");
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
    catch (Exception ex)
    {
        error = new { code = -32601, message = ex.Message };
        result = null;
    }

    var envelope = error is null
        ? (object)new { jsonrpc = "2.0", id = id?.ToJsonString(), result }
        : new { jsonrpc = "2.0", id = id?.ToJsonString(), error };

    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync(JsonSerializer.Serialize(envelope));
});

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
