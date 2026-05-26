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
