// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Mcp;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the MCP protocol plugin. Identity, defensive guards on
/// <c>DiscoverAsync</c>, the JSON-RPC JsonElement extractors, and the
/// adapter server's stateless message handlers (initialize / ping /
/// unknown method) are all reachable without a live MCP server. Real
/// JSON-RPC against an HTTP endpoint is integration-tier territory.
/// </summary>
public sealed class BowireMcpProtocolTests
{
    [Fact]
    public void Identity_Properties_Are_Stable()
    {
        var protocol = new BowireMcpProtocol();

        Assert.Equal("MCP", protocol.Name);
        Assert.Equal("mcp", protocol.Id);
        Assert.NotNull(protocol.IconSvg);
        Assert.Contains("<svg", protocol.IconSvg, StringComparison.Ordinal);
    }

    [Fact]
    public void Implements_IBowireProtocol()
    {
        var protocol = new BowireMcpProtocol();

        Assert.IsAssignableFrom<IBowireProtocol>(protocol);
    }

    [Fact]
    public async Task DiscoverAsync_Empty_Url_Returns_Empty()
    {
        var protocol = new BowireMcpProtocol();

        var services = await protocol.DiscoverAsync(
            "", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Whitespace_Url_Returns_Empty()
    {
        var protocol = new BowireMcpProtocol();

        var services = await protocol.DiscoverAsync(
            "   ", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Unreachable_Endpoint_Returns_Empty_Without_Throwing()
    {
        var protocol = new BowireMcpProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var services = await protocol.DiscoverAsync(
            "http://127.0.0.2:1", showInternalServices: false, cts.Token);

        Assert.Empty(services);
    }

    [Fact]
    public async Task InvokeAsync_Unreachable_Endpoint_Returns_Error_Status_Without_Throwing()
    {
        var protocol = new BowireMcpProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var result = await protocol.InvokeAsync(
            "http://127.0.0.2:1",
            "Tools",
            "myTool",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            cts.Token);

        Assert.NotNull(result);
        // The exception message becomes the Status; "OK" only happens on the
        // happy path which we can't reach without a real server.
        Assert.NotEqual("OK", result.Status);
    }

    [Fact]
    public async Task InvokeStreamAsync_Yields_Nothing_Because_Mcp_Is_Unary()
    {
        var protocol = new BowireMcpProtocol();

        var collected = new List<string>();
        await foreach (var item in protocol.InvokeStreamAsync(
            "http://localhost:5000", "Tools", "x", ["{}"], false, null,
            TestContext.Current.CancellationToken))
        {
            collected.Add(item);
        }

        Assert.Empty(collected);
    }

    [Fact]
    public async Task OpenChannelAsync_Returns_Null_Because_Mcp_Has_No_Channel_Surface()
    {
        var protocol = new BowireMcpProtocol();

        var channel = await protocol.OpenChannelAsync(
            "http://localhost:5000", "Tools", "x", false, null,
            TestContext.Current.CancellationToken);

        Assert.Null(channel);
    }

    [Fact]
    public void ResolveEndpoint_Trims_Trailing_Slash()
    {
        var resolved = InvokeResolveEndpoint("http://localhost:5005/");

        Assert.Equal("http://localhost:5005", resolved);
    }

    [Fact]
    public void ResolveEndpoint_Multiple_Trailing_Slashes_Are_All_Stripped()
    {
        var resolved = InvokeResolveEndpoint("http://localhost:5005///");

        Assert.Equal("http://localhost:5005", resolved);
    }

    [Fact]
    public void ParseArguments_Empty_List_Returns_Default_JsonElement()
    {
        var element = InvokeParseArguments(new List<string>());

        Assert.Equal(JsonValueKind.Undefined, element.ValueKind);
    }

    [Fact]
    public void ParseArguments_Whitespace_Returns_Default_JsonElement()
    {
        var element = InvokeParseArguments(new List<string> { "   " });

        Assert.Equal(JsonValueKind.Undefined, element.ValueKind);
    }

    [Fact]
    public void ParseArguments_Bad_Json_Returns_Default_JsonElement()
    {
        var element = InvokeParseArguments(new List<string> { "{not json}" });

        Assert.Equal(JsonValueKind.Undefined, element.ValueKind);
    }

    [Fact]
    public void ParseArguments_Valid_Json_Returns_Object_Element()
    {
        var element = InvokeParseArguments(new List<string> { """{"a":1}""" });

        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal(1, element.GetProperty("a").GetInt32());
    }

    [Fact]
    public void ExtractTools_Empty_List_Returns_Empty()
    {
        var json = JsonSerializer.SerializeToElement(new { tools = Array.Empty<object>() });

        var methods = InvokeExtractTools(json);

        Assert.Empty(methods);
    }

    [Fact]
    public void ExtractTools_Tool_Without_Name_Falls_Back_To_Sentinel()
    {
        // Tool missing a "name" — the extractor uses "tool" as the sentinel.
        var json = JsonSerializer.SerializeToElement(new
        {
            tools = new[] { new { description = "no-name" } }
        });

        var methods = InvokeExtractTools(json);

        var method = Assert.Single(methods);
        Assert.Equal("tool", method.Name);
        Assert.Equal("Tools/tool", method.FullName);
    }

    [Fact]
    public void ExtractTools_Single_Tool_With_InputSchema_Maps_Fields()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            tools = new[]
            {
                new
                {
                    name = "weather",
                    description = "Get weather",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            city = new { type = "string" }
                        }
                    }
                }
            }
        });

        var methods = InvokeExtractTools(json);

        var method = Assert.Single(methods);
        Assert.Equal("weather", method.Name);
        Assert.Equal("Tools/weather", method.FullName);
        Assert.Equal("Get weather", method.Description);
        Assert.Equal("Unary", method.MethodType);
        Assert.Single(method.InputType.Fields);
    }

    [Fact]
    public void ExtractTools_Without_InputSchema_Yields_Empty_Input()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            tools = new[] { new { name = "noInput" } }
        });

        var methods = InvokeExtractTools(json);
        var method = Assert.Single(methods);

        Assert.Equal("noInputInput", method.InputType.Name);
        Assert.Empty(method.InputType.Fields);
    }

    [Fact]
    public void ExtractTools_Missing_Tools_Property_Returns_Empty()
    {
        var json = JsonSerializer.SerializeToElement(new { other = 1 });

        var methods = InvokeExtractTools(json);

        Assert.Empty(methods);
    }

    [Fact]
    public void ExtractResources_Maps_Uri_To_Method_Name()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            resources = new[]
            {
                new { uri = "config://app/settings", name = "App Settings", description = "live config" }
            }
        });

        var methods = InvokeExtractResources(json);

        var method = Assert.Single(methods);
        Assert.Equal("config://app/settings", method.Name);
        Assert.Equal("Resources/config://app/settings", method.FullName);
        Assert.Contains("App Settings", method.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractResources_Missing_Property_Returns_Empty()
    {
        var json = JsonSerializer.SerializeToElement(new { });

        var methods = InvokeExtractResources(json);

        Assert.Empty(methods);
    }

    [Fact]
    public void ExtractPrompts_Builds_Argument_Fields_With_Required_Flag()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            prompts = new[]
            {
                new
                {
                    name = "review",
                    description = "Review a doc",
                    arguments = new object[]
                    {
                        new { name = "doc", required = true,  description = "The document" },
                        new { name = "tone", required = false }
                    }
                }
            }
        });

        var methods = InvokeExtractPrompts(json);
        var method = Assert.Single(methods);

        Assert.Equal("review", method.Name);
        Assert.Equal("Prompts/review", method.FullName);
        Assert.Equal(2, method.InputType.Fields.Count);

        var docField = method.InputType.Fields.Single(f => f.Name == "doc");
        Assert.True(docField.Required);
        Assert.Equal("body", docField.Source);

        var toneField = method.InputType.Fields.Single(f => f.Name == "tone");
        Assert.False(toneField.Required);
    }

    [Fact]
    public void ExtractPrompts_Without_Arguments_Yields_Empty_Input_Fields()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            prompts = new[] { new { name = "freebie" } }
        });

        var methods = InvokeExtractPrompts(json);
        var method = Assert.Single(methods);

        Assert.Empty(method.InputType.Fields);
    }

    [Fact]
    public void ExtractPrompts_Missing_Prompts_Property_Returns_Empty()
    {
        var json = JsonSerializer.SerializeToElement(new { });

        Assert.Empty(InvokeExtractPrompts(json));
    }

    private static string InvokeResolveEndpoint(string url)
    {
        var method = typeof(BowireMcpProtocol).GetMethod(
            "ResolveEndpoint", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object[] { url })!;
    }

    private static JsonElement InvokeParseArguments(List<string> jsonMessages)
    {
        var method = typeof(BowireMcpProtocol).GetMethod(
            "ParseArguments", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (JsonElement)method!.Invoke(null, new object[] { jsonMessages })!;
    }

    private static List<BowireMethodInfo> InvokeExtractTools(JsonElement input)
        => InvokeExtract("ExtractTools", input);

    private static List<BowireMethodInfo> InvokeExtractResources(JsonElement input)
        => InvokeExtract("ExtractResources", input);

    private static List<BowireMethodInfo> InvokeExtractPrompts(JsonElement input)
        => InvokeExtract("ExtractPrompts", input);

    private static List<BowireMethodInfo> InvokeExtract(string name, JsonElement input)
    {
        var method = typeof(BowireMcpProtocol).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, new object[] { input });
        return Assert.IsType<List<BowireMethodInfo>>(result);
    }
}

/// <summary>
/// Tests for <see cref="McpAdapterServer"/> — the JSON-RPC handler that
/// turns an MCP message into a Bowire-protocol invocation. Initialize /
/// ping / unknown-method are stateless (no protocol discovery happens), so
/// they're reachable without any registered protocols.
/// </summary>
public sealed class McpAdapterServerTests
{
    [Fact]
    public async Task HandleMessageAsync_Initialize_Returns_Server_Capabilities()
    {
        var server = new McpAdapterServer(new BowireProtocolRegistry(), "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize"
        });

        var result = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal("2.0", result.GetProperty("jsonrpc").GetString());
        var resultObj = result.GetProperty("result");
        Assert.Equal("2024-11-05", resultObj.GetProperty("protocolVersion").GetString());
        Assert.Equal("bowire", resultObj.GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task HandleMessageAsync_Notifications_Initialized_Returns_No_Response()
    {
        var server = new McpAdapterServer(new BowireProtocolRegistry(), "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        });

        var result = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);

        // Notifications return default(JsonElement)
        Assert.Equal(JsonValueKind.Undefined, result.ValueKind);
    }

    [Fact]
    public async Task HandleMessageAsync_Ping_Returns_Empty_Object_Response()
    {
        var server = new McpAdapterServer(new BowireProtocolRegistry(), "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 7,
            method = "ping"
        });

        var result = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal(JsonValueKind.Object, result.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task HandleMessageAsync_Unknown_Method_Returns_Method_Not_Found_Error()
    {
        var server = new McpAdapterServer(new BowireProtocolRegistry(), "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 9,
            method = "totally/madeup"
        });

        var result = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);

        // The result is wrapped twice: outer envelope is a "response" with a
        // "result" property that is itself the JSON-RPC error envelope.
        // CreateResponse always wraps via CreateResponse.
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        var inner = result.GetProperty("result");
        Assert.True(inner.TryGetProperty("error", out var error));
        Assert.Equal(-32601, error.GetProperty("code").GetInt32());
        Assert.Contains("totally/madeup", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleMessageAsync_Tools_List_With_No_Protocols_Returns_Empty_Tools()
    {
        var server = new McpAdapterServer(new BowireProtocolRegistry(), "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/list"
        });

        var result = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);

        var tools = result.GetProperty("result").GetProperty("tools");
        Assert.Equal(JsonValueKind.Array, tools.ValueKind);
        Assert.Equal(0, tools.GetArrayLength());
    }

    [Fact]
    public async Task HandleMessageAsync_Tools_Call_Unknown_Tool_Returns_Tool_Not_Found_Error()
    {
        var server = new McpAdapterServer(new BowireProtocolRegistry(), "http://localhost");
        var msg = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new { name = "Nope", arguments = new { } }
        });

        var result = await server.HandleMessageAsync(msg, TestContext.Current.CancellationToken);

        // Tool not found returns the error directly (no second wrapping).
        var inner = result.GetProperty("result");
        Assert.True(inner.TryGetProperty("error", out var error));
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("string", "string")]
    [InlineData("bool", "boolean")]
    [InlineData("int32", "integer")]
    [InlineData("int64", "integer")]
    [InlineData("uint32", "integer")]
    [InlineData("sint64", "integer")]
    [InlineData("fixed32", "integer")]
    [InlineData("double", "number")]
    [InlineData("float", "number")]
    [InlineData("bytes", "string")]
    [InlineData("enum", "string")]
    [InlineData("message", "object")]
    [InlineData("unknown-type", "string")]
    public void MapToJsonSchemaType_Maps_Bowire_Types_To_JsonSchema_Shapes(string proto, string jsonSchema)
    {
        var method = typeof(McpAdapterServer).GetMethod(
            "MapToJsonSchemaType", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, new object[] { proto });

        Assert.Equal(jsonSchema, result);
    }
}
