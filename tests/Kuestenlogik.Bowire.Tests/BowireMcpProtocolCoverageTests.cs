// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Mcp;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Coverage-targeted tests for the MCP protocol plugin: drives the JSON-RPC
/// envelope edge cases (error / missing-result), the SSE reader's line
/// parsing, the input-schema mapper's non-object and array/items branches,
/// and the adapter-server's tools/list + tools/call dispatch with a fake
/// in-process protocol so we don't need the network.
/// </summary>
public sealed class BowireMcpProtocolCoverageTests
{
    private static readonly string[] s_requiredId = ["id"];

    [Fact]
    public void ExtractResult_Error_Envelope_Throws_With_Code_And_Message()
    {
        var envelope = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 1,
            error = new { code = -32601, message = "boom" }
        });

        var ex = Assert.Throws<TargetInvocationException>(() => InvokeExtractResult(envelope, 1));
        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("-32601", inner.Message, StringComparison.Ordinal);
        Assert.Contains("boom", inner.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractResult_Error_With_Missing_Code_And_Message_Uses_Defaults()
    {
        // Both code and message missing -> -1 / "Unknown MCP error" defaults.
        var envelope = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 1,
            error = new { }
        });

        var ex = Assert.Throws<TargetInvocationException>(() => InvokeExtractResult(envelope, 1));
        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("-1", inner.Message, StringComparison.Ordinal);
        Assert.Contains("Unknown MCP error", inner.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractResult_Returns_Result_When_Present()
    {
        var envelope = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 5,
            result = new { ok = true }
        });

        var result = InvokeExtractResult(envelope, 5);

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void ExtractResult_No_Result_No_Error_Throws()
    {
        var envelope = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 9 });

        var ex = Assert.Throws<TargetInvocationException>(() => InvokeExtractResult(envelope, 9));
        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("9", inner.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadSseResponseAsync_Returns_Result_When_Id_Matches()
    {
        var sse = string.Join('\n', [
            "event: message",
            "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"answer\":42}}",
            "",
            ""
        ]);

        var result = await InvokeReadSseAsync(sse, expectedId: 1);

        Assert.Equal(42, result.GetProperty("answer").GetInt32());
    }

    [Fact]
    public async Task ReadSseResponseAsync_Skips_Mismatched_Id_Then_Returns_Match()
    {
        // First event is a different id -> skipped; second event matches.
        var sse = string.Join('\n', [
            "data: {\"jsonrpc\":\"2.0\",\"id\":99,\"result\":{\"x\":1}}",
            "",
            "data: {\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{\"x\":2}}",
            "",
            ""
        ]);

        var result = await InvokeReadSseAsync(sse, expectedId: 7);

        Assert.Equal(2, result.GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task ReadSseResponseAsync_Stream_Closed_Without_Match_Throws()
    {
        var sse = "data: {\"jsonrpc\":\"2.0\",\"id\":99,\"result\":{}}\n\n";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeReadSseAsync(sse, expectedId: 1));
        Assert.Contains("SSE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadSseResponseAsync_Ignores_Non_Message_Events()
    {
        // The "ping" event must be skipped; only the "message" frame is consumed.
        var sse = string.Join('\n', [
            "event: ping",
            "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"x\":1}}",
            "",
            "event: message",
            "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"x\":2}}",
            "",
            ""
        ]);

        var result = await InvokeReadSseAsync(sse, expectedId: 1);

        Assert.Equal(2, result.GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task ReadSseResponseAsync_Handles_Bare_Data_Line_Without_Space()
    {
        // "data:{...}" (no space after colon) is valid SSE and the reader
        // must handle the colon-only prefix branch (line.Length > 5 false).
        var sse = "data:\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n";

        var result = await InvokeReadSseAsync(sse, expectedId: 1);

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
    }

    [Fact]
    public void MapInputSchema_Non_Object_Schema_Returns_Empty()
    {
        var notObject = JsonSerializer.SerializeToElement("scalar");

        var info = McpDiscoveryClient.MapInputSchema("toolName", notObject);

        Assert.Equal("toolNameInput", info.Name);
        Assert.Empty(info.Fields);
    }

    [Fact]
    public void MapInputSchema_Maps_All_Json_Schema_Types()
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            required = s_requiredId,
            properties = new Dictionary<string, object>
            {
                ["id"] = new { type = "integer", description = "row id" },
                ["score"] = new { type = "number" },
                ["enabled"] = new { type = "boolean" },
                ["name"] = new { type = "string" },
                ["weird"] = new { type = "null" },
                ["nested"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["inner"] = new { type = "string" }
                    }
                },
                ["tags"] = new
                {
                    type = "array",
                    items = new { type = "string" }
                },
                ["nakedArray"] = new { type = "array" }
            }
        });

        var info = McpDiscoveryClient.MapInputSchema("ToolX", schema);

        var byName = info.Fields.ToDictionary(f => f.Name);

        Assert.Equal("int64", byName["id"].Type);
        Assert.True(byName["id"].Required);
        Assert.Equal("required", byName["id"].Label);
        Assert.Equal("row id", byName["id"].Description);

        Assert.Equal("double", byName["score"].Type);
        Assert.False(byName["score"].Required);
        Assert.Equal("optional", byName["score"].Label);

        Assert.Equal("bool", byName["enabled"].Type);
        Assert.Equal("string", byName["name"].Type);

        // Unknown/missing type -> "string"
        Assert.Equal("string", byName["weird"].Type);

        Assert.Equal("message", byName["nested"].Type);
        Assert.NotNull(byName["nested"].MessageType);
        Assert.Single(byName["nested"].MessageType!.Fields);

        Assert.Equal("string", byName["tags"].Type);
        Assert.True(byName["tags"].IsRepeated);

        // Array without "items" still flagged repeated, falls back to string
        Assert.Equal("string", byName["nakedArray"].Type);
        Assert.True(byName["nakedArray"].IsRepeated);
    }

    [Fact]
    public void MapInputSchema_Array_Items_Non_Object_Falls_Back_To_String()
    {
        // Reaches ResolvePropertyType's non-object early-return through the
        // recursive array/items branch.
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["weird"] = new
                {
                    type = "array",
                    items = "scalar"
                }
            }
        });

        var info = McpDiscoveryClient.MapInputSchema("ToolY", schema);
        var field = Assert.Single(info.Fields);

        Assert.Equal("string", field.Type);
        Assert.True(field.IsRepeated);
    }

    [Fact]
    public async Task AdapterServer_ToolsList_Includes_Fake_Protocol_Tool_With_InputSchema()
    {
        var fake = new FakeProtocol("rest", "REST", new BowireServiceInfo("Greet", "demo", new List<BowireMethodInfo>
        {
            BuildMethod("hello", new BowireMessageInfo("HelloInput", "demo.HelloInput", new List<BowireFieldInfo>
            {
                new("name", 1, "string", "required", IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null),
                new("count", 2, "int32", "optional", IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null),
                new("flags", 3, "string", "required", IsMap: false, IsRepeated: true, MessageType: null, EnumValues: null)
            }))
        }));

        var registry = new BowireProtocolRegistry();
        registry.Register(fake);
        registry.Register(new ThrowingProtocol("graphql", "GraphQL"));
        registry.Register(new SkipMeMcpProtocol());

        var server = new McpAdapterServer(registry, "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        var tools = response.GetProperty("result").GetProperty("tools");

        Assert.Equal(1, tools.GetArrayLength());
        var tool = tools[0];
        Assert.Equal("Greet_hello", tool.GetProperty("name").GetString());
        var schema = tool.GetProperty("inputSchema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var props = schema.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("integer", props.GetProperty("count").GetProperty("type").GetString());

        var required = schema.GetProperty("required");
        Assert.Equal(JsonValueKind.Array, required.ValueKind);
        var requiredNames = required.EnumerateArray().Select(r => r.GetString()).ToArray();
        Assert.Contains("name", requiredNames);
        // Repeated/map fields are never marked required even if labelled "required".
        Assert.DoesNotContain("flags", requiredNames);
        Assert.DoesNotContain("count", requiredNames);
    }

    [Fact]
    public async Task AdapterServer_ToolsList_Skips_Streaming_Methods_And_Mcp_Plugin()
    {
        // Streaming methods are filtered out, and the MCP plugin itself is skipped.
        var fake = new FakeProtocol("grpc", "gRPC", new BowireServiceInfo("Stream", "demo", new List<BowireMethodInfo>
        {
            new("watch", "demo.Stream/watch",
                ClientStreaming: false, ServerStreaming: true,
                InputType: new BowireMessageInfo("WatchInput", "demo.WatchInput", []),
                OutputType: new BowireMessageInfo("WatchOutput", "demo.WatchOutput", []),
                MethodType: "ServerStreaming")
        }));

        var registry = new BowireProtocolRegistry();
        registry.Register(fake);

        var server = new McpAdapterServer(registry, "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        var tools = response.GetProperty("result").GetProperty("tools");

        Assert.Equal(0, tools.GetArrayLength());
    }

    [Fact]
    public async Task AdapterServer_ToolsList_Drops_Protocol_That_Throws_On_Discover()
    {
        // Throwing protocols don't blow up the whole tools/list -- they're just skipped.
        var registry = new BowireProtocolRegistry();
        registry.Register(new ThrowingProtocol("ws", "WebSocket"));

        var server = new McpAdapterServer(registry, "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        var tools = response.GetProperty("result").GetProperty("tools");

        Assert.Equal(0, tools.GetArrayLength());
    }

    [Fact]
    public async Task AdapterServer_ToolsCall_Routes_To_Matching_Method_And_Wraps_Result()
    {
        var fake = new FakeProtocol("rest", "REST", new BowireServiceInfo("Greet", "demo", new List<BowireMethodInfo>
        {
            BuildMethod("hello", new BowireMessageInfo("In", "In", []))
        }))
        {
            InvokeResult = new InvokeResult("\"hi there\"", 1, "OK", new Dictionary<string, string>())
        };

        var registry = new BowireProtocolRegistry();
        registry.Register(fake);

        var server = new McpAdapterServer(registry, "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new { name = "Greet_hello", arguments = new { who = "world" } }
        });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        var content = response.GetProperty("result").GetProperty("content");

        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("\"hi there\"", content[0].GetProperty("text").GetString());

        Assert.Equal("Greet", fake.LastService);
        Assert.Equal("hello", fake.LastMethod);
        Assert.Single(fake.LastJsonMessages!);
        // Arguments survived the JsonElement -> raw text round-trip.
        Assert.Contains("\"who\":\"world\"", fake.LastJsonMessages![0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdapterServer_ToolsCall_Default_Arguments_When_Omitted_Sends_Empty_Object()
    {
        var fake = new FakeProtocol("rest", "REST", new BowireServiceInfo("S", "demo", new List<BowireMethodInfo>
        {
            BuildMethod("m", new BowireMessageInfo("In", "In", []))
        }))
        {
            InvokeResult = new InvokeResult(null, 0, "OK", new Dictionary<string, string>())
        };

        var registry = new BowireProtocolRegistry();
        registry.Register(fake);

        var server = new McpAdapterServer(registry, "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new { name = "S_m" }
        });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        var content = response.GetProperty("result").GetProperty("content");

        Assert.Equal("null", content[0].GetProperty("text").GetString());
        Assert.NotNull(fake.LastJsonMessages);
        // Default empty-args object sent when "arguments" is missing.
        Assert.Equal("{}", fake.LastJsonMessages![0]);
    }

    [Fact]
    public async Task AdapterServer_ToolsCall_Wraps_Plugin_Exception_As_IsError_Result()
    {
        var fake = new FakeProtocol("rest", "REST", new BowireServiceInfo("S", "demo", new List<BowireMethodInfo>
        {
            BuildMethod("m", new BowireMessageInfo("In", "In", []))
        }))
        {
            InvokeException = new InvalidOperationException("plugin exploded")
        };

        var registry = new BowireProtocolRegistry();
        registry.Register(fake);

        var server = new McpAdapterServer(registry, "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "tools/call",
            @params = new { name = "S_m", arguments = new { } }
        });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        var inner = response.GetProperty("result");

        Assert.True(inner.GetProperty("isError").GetBoolean());
        var text = inner.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("plugin exploded", text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdapterServer_ToolsCall_Skips_Throwing_Discover_And_Mcp_Plugin()
    {
        // Both protocols here yield no callable tool: one is the MCP plugin
        // (skipped), one throws on discovery. Result must be tool-not-found.
        var registry = new BowireProtocolRegistry();
        registry.Register(new SkipMeMcpProtocol());
        registry.Register(new ThrowingProtocol("rest", "REST"));

        var server = new McpAdapterServer(registry, "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 5,
            method = "tools/call",
            @params = new { name = "Anything_here", arguments = new { } }
        });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);
        var inner = response.GetProperty("result");

        Assert.True(inner.TryGetProperty("error", out var error));
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task AdapterServer_ToolsCall_Skips_Mismatched_Method_Names()
    {
        var fake = new FakeProtocol("rest", "REST", new BowireServiceInfo("Greet", "demo", new List<BowireMethodInfo>
        {
            BuildMethod("hello", new BowireMessageInfo("In", "In", []))
        }));

        var registry = new BowireProtocolRegistry();
        registry.Register(fake);

        var server = new McpAdapterServer(registry, "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 6,
            method = "tools/call",
            @params = new { name = "Greet_other", arguments = new { } }
        });

        var response = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);

        Assert.True(response.GetProperty("result").TryGetProperty("error", out var error));
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task BowireMcpProtocol_InvokeAsync_Unknown_Service_Returns_Error_Status()
    {
        // 'Servers' is not Tools/Resources/Prompts -> hits the default switch
        // arm. We use an unreachable URL so the InvokeAsync fast-fails inside
        // InitializeAsync before reaching the switch... so we need a server
        // that DOES respond to initialize. Hand-rolled minimal HTTP server.
        var protocol = new BowireMcpProtocol();

        var result = await protocol.InvokeAsync(
            "http://127.0.0.2:1",
            "Servers",
            "x",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            CancellationToken.None);

        Assert.NotEqual("OK", result.Status);
    }

    private static BowireMethodInfo BuildMethod(string name, BowireMessageInfo input)
        => new(
            Name: name,
            FullName: "demo." + name,
            ClientStreaming: false,
            ServerStreaming: false,
            InputType: input,
            OutputType: new BowireMessageInfo(name + "Result", "demo." + name + "Result", []),
            MethodType: "Unary");

    private static JsonElement InvokeExtractResult(JsonElement envelope, int expectedId)
    {
        var method = typeof(McpDiscoveryClient).GetMethod(
            "ExtractResult", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (JsonElement)method!.Invoke(null, [envelope, expectedId])!;
    }

    private static async Task<JsonElement> InvokeReadSseAsync(string body, int expectedId)
    {
        var method = typeof(McpDiscoveryClient).GetMethod(
            "ReadSseResponseAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(body));
        using var reader = new StreamReader(ms);
        var task = (Task<JsonElement>)method!.Invoke(null, [reader, expectedId, CancellationToken.None])!;
        return await task;
    }

    private sealed class FakeProtocol(string id, string name, BowireServiceInfo service) : IBowireProtocol
    {
        private readonly BowireServiceInfo _service = service;

        public string Name => name;
        public string Id => id;
        public string IconSvg => "<svg/>";

        public string? LastService { get; private set; }
        public string? LastMethod { get; private set; }
        public List<string>? LastJsonMessages { get; private set; }

        public InvokeResult? InvokeResult { get; init; }
        public Exception? InvokeException { get; init; }

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo> { _service });

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            LastService = service;
            LastMethod = method;
            LastJsonMessages = jsonMessages;
            if (InvokeException is not null) throw InvokeException;
            return Task.FromResult(InvokeResult ?? new InvokeResult(null, 0, "OK", new Dictionary<string, string>()));
        }

        public IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<string>();

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }

    private sealed class ThrowingProtocol(string id, string name) : IBowireProtocol
    {
        public string Name => name;
        public string Id => id;
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => throw new InvalidOperationException("discover failed: " + name + " " + CultureInfo.InvariantCulture.Name);

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new InvalidOperationException("never called");

        public IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<string>();

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }

    /// <summary>Stand-in MCP plugin -- the adapter must skip it during scans.</summary>
    private sealed class SkipMeMcpProtocol : IBowireProtocol
    {
        public string Name => "MCP";
        public string Id => "mcp";
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => throw new InvalidOperationException("MCP plugin should never be asked to discover for the adapter.");

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new InvalidOperationException("never called");

        public IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<string>();

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }
}
