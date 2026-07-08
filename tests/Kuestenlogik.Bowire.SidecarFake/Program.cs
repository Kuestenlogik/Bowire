// A tiny fake sidecar plugin that follows the JSON-RPC-over-stdio
// contract documented in docs/architecture/sidecar-plugins.md.
// Used by SidecarBowireProtocolIntegrationTests as a process target
// so the test can exercise spawn / initialize / discover / invoke /
// invokeStream / shutdown without depending on Python, Node, etc.
//
// Behaviour:
//   initialize  -> { id:"fake", name:"Fake", iconSvg:"<svg/>", protocolVersion:1, capabilities:{...} }
//   discover    -> one service with one method called "echo"
//   invoke      -> echoes the first jsonMessage back as response
//   invokeStream-> emits 3 $/stream/data notifications then $/stream/end
//                  (uses the host-provided streamId from params)
//   openChannel -> ack with the host-provided channelId
//   channel.send-> ack + echoes "ack: <msg>" as a $/channel/data frame
//   channel.close-> ack + $/channel/closed notification
//   ping        -> "pong"
//   shutdown    -> Environment.Exit(0)
//
// Uses System.Text.Json directly so the binary has no runtime deps
// beyond the BCL.

using System.Text.Json;
using System.Text.Json.Nodes;

// Test knobs (#416 handshake): the integration tests pass these via the
// manifest's `args` to drive the version-gate + capability paths.
//   --protocol-version <n>  advertise this sidecar contract version (default 1)
//   --no-channels           advertise capabilities.channels = false
//   --legacy                omit protocolVersion + capabilities entirely (pre-#416 sidecar)
var advertiseHandshake = true;
var protocolVersion = 1;
var channelsCapability = true;
for (var ai = 0; ai < args.Length; ai++)
{
    switch (args[ai])
    {
        case "--protocol-version" when ai + 1 < args.Length && int.TryParse(args[ai + 1], out var pv):
            protocolVersion = pv; ai++; break;
        case "--no-channels": channelsCapability = false; break;
        case "--legacy": advertiseHandshake = false; break;
    }
}

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
using var reader = new StreamReader(stdin, new System.Text.UTF8Encoding(false));
using var writer = new StreamWriter(stdout, new System.Text.UTF8Encoding(false)) { AutoFlush = true };

string? line;
while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    var node = JsonNode.Parse(line) as JsonObject;
    if (node is null) continue;

    var method = node["method"]?.GetValue<string>() ?? "";
    var id = node["id"]?.GetValue<long>();
    var p = node["params"] as JsonObject;

    switch (method)
    {
        case "initialize":
            if (advertiseHandshake)
            {
                await Reply(id, new
                {
                    name = "Fake",
                    id = "fake",
                    iconSvg = "<svg/>",
                    protocolVersion,
                    capabilities = new { discover = true, invoke = true, invokeStream = true, channels = channelsCapability },
                });
            }
            else
            {
                // Legacy sidecar: no protocolVersion / capabilities advertised.
                await Reply(id, new { name = "Fake", id = "fake", iconSvg = "<svg/>" });
            }
            break;

        case "ping":
            await Reply(id, "pong");
            break;

        case "shutdown":
            await Reply(id, true);
            Environment.Exit(0);
            return;

        case "discover":
            await Reply(id, new[]
            {
                new
                {
                    name = "Echo",
                    package = "fake",
                    methods = new[]
                    {
                        new
                        {
                            name = "echo",
                            fullName = "Echo/echo",
                            clientStreaming = false,
                            serverStreaming = false,
                            inputType = new { name = "EchoIn", fullName = "EchoIn", fields = Array.Empty<object>() },
                            outputType = new { name = "EchoOut", fullName = "EchoOut", fields = Array.Empty<object>() },
                            methodType = "Unary",
                        },
                    },
                    source = "fake",
                    originUrl = p?["serverUrl"]?.GetValue<string>(),
                },
            });
            break;

        case "invoke":
            var msgs = p?["jsonMessages"] as JsonArray;
            var first = msgs?.Count > 0 ? msgs[0]?.GetValue<string>() : "";
            await Reply(id, new
            {
                response = "echo: " + first,
                durationMs = 1L,
                status = "OK",
                metadata = new { source = "fake" },
            });
            break;

        case "invokeStream":
            // Host generates the streamId now; echo it on every frame.
            var streamId = p?["streamId"]?.GetValue<string>() ?? "s";
            await Reply(id, new { streamId });
            for (var i = 1; i <= 3; i++)
            {
                await Notify("$/stream/data", new { streamId, message = "tick-" + i });
            }
            await Notify("$/stream/end", new { streamId, error = (object?)null });
            break;

        case "openChannel":
            var channelId = p?["channelId"]?.GetValue<string>() ?? "c";
            await Reply(id, new { channelId });
            break;

        case "channel.send":
            var cid = p?["channelId"]?.GetValue<string>() ?? "c";
            var sent = p?["message"]?.GetValue<string>() ?? "";
            await Reply(id, true);
            // Echo the message straight back as an inbound frame so the
            // duplex round-trip is observable from the test.
            await Notify("$/channel/data", new { channelId = cid, message = "ack: " + sent });
            break;

        case "channel.close":
            var ccid = p?["channelId"]?.GetValue<string>() ?? "c";
            await Reply(id, true);
            await Notify("$/channel/closed", new { channelId = ccid });
            break;

        default:
            await ReplyError(id, -32601, "method not found: " + method);
            break;
    }
}

async Task Reply(long? id, object result)
{
    var env = new { jsonrpc = "2.0", id, result };
    await writer.WriteLineAsync(JsonSerializer.Serialize(env));
}

async Task ReplyError(long? id, int code, string message)
{
    var env = new { jsonrpc = "2.0", id, error = new { code, message } };
    await writer.WriteLineAsync(JsonSerializer.Serialize(env));
}

async Task Notify(string method, object @params)
{
    var env = new { jsonrpc = "2.0", method, @params };
    await writer.WriteLineAsync(JsonSerializer.Serialize(env));
}
