// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end tests for <see cref="BowireMcpProtocol"/> against a hand-rolled
/// MCP server hosted on a real Kestrel port. Verifies discovery, tool call,
/// and forwarding of metadata headers (the auth pipeline) on every JSON-RPC
/// request the client sends.
/// </summary>
public sealed class McpProtocolIntegrationTests
{
    private static readonly string[] AddRequiredArgs = ["a", "b"];
    private static readonly string[] EchoRequiredArgs = ["message"];

    [Fact]
    public async Task Discover_Returns_Tools_Resources_And_Prompts_Services()
    {
        await using var host = await PluginTestHost.StartAsync(MapMcpEndpoint);
        var protocol = new BowireMcpProtocol();

        var services = await protocol.DiscoverAsync(host.BaseUrl + "/mcp", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Equal(["Tools", "Resources", "Prompts"], services.Select(s => s.Name).ToArray());

        var tools = services.Single(s => s.Name == "Tools");
        Assert.Equal(["echo", "add"], tools.Methods.Select(m => m.Name).ToArray());

        var add = tools.Methods.Single(m => m.Name == "add");
        Assert.Equal(2, add.InputType.Fields.Count);
        Assert.Contains(add.InputType.Fields, f => f is { Name: "a", Required: true });
        Assert.Contains(add.InputType.Fields, f => f is { Name: "b", Required: true });
    }

    [Fact]
    public async Task Invoke_Tool_Call_Returns_Server_Response()
    {
        await using var host = await PluginTestHost.StartAsync(MapMcpEndpoint);
        var protocol = new BowireMcpProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/mcp",
            service: "Tools",
            method: "add",
            jsonMessages: ["{\"a\":21,\"b\":21}"],
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        // The fake server echoes the sum as plain-text content
        Assert.Contains("\"text\": \"42\"", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_Tool_Call_Forwards_Metadata_As_Http_Headers()
    {
        // The fake server records every header on every request and exposes
        // them via the `echo` tool — we send a Bearer token through the
        // metadata dict and check that it arrived on the wire.
        await using var host = await PluginTestHost.StartAsync(MapMcpEndpoint);
        var protocol = new BowireMcpProtocol();

        var metadata = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer test-token-xyz"
        };

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/mcp",
            service: "Tools",
            method: "echo",
            jsonMessages: ["{\"message\":\"check headers\"}"],
            showInternalServices: false,
            metadata: metadata,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        // The echo tool returns "saw header: <Authorization value>" so we
        // know the header reached the server.
        Assert.Contains("Bearer test-token-xyz", result.Response, StringComparison.Ordinal);
    }

    /// <summary>
    /// Minimal MCP server: speaks the streamable HTTP transport with a fixed
    /// set of tools, resources and prompts. Records the most recent
    /// Authorization header so test (3) can verify metadata propagation.
    /// </summary>
    private static void MapMcpEndpoint(WebApplication app)
    {
        string? lastAuthHeader = null;

        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            lastAuthHeader = ctx.Request.Headers.Authorization.ToString();

            var message = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = message.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = message.TryGetProperty("id", out var idProp) ? (JsonElement?)idProp : null;

            object? result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "fake-mcp", version = "0.1.0" }
                },
                "notifications/initialized" => null,
                "tools/list" => new
                {
                    tools = new object[]
                    {
                        new
                        {
                            name = "echo",
                            description = "Echo a message back to the caller.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["message"] = new { type = "string" }
                                },
                                required = EchoRequiredArgs
                            }
                        },
                        new
                        {
                            name = "add",
                            description = "Add two numbers",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["a"] = new { type = "number" },
                                    ["b"] = new { type = "number" }
                                },
                                required = AddRequiredArgs
                            }
                        }
                    }
                },
                "tools/call" => HandleToolCall(message, lastAuthHeader),
                "resources/list" => new
                {
                    resources = new object[]
                    {
                        new { uri = "notes://welcome", name = "Welcome", description = "First note" }
                    }
                },
                "prompts/list" => new
                {
                    prompts = new object[]
                    {
                        new
                        {
                            name = "greet",
                            description = "Friendly greeting",
                            arguments = new object[]
                            {
                                new { name = "name", description = "Person", required = true }
                            }
                        }
                    }
                },
                _ => null
            };

            if (result is null && method == "notifications/initialized")
            {
                ctx.Response.StatusCode = 204;
                return;
            }

            if (result is null)
            {
                await ctx.Response.WriteAsJsonAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32601, message = $"Unknown method: {method}" }
                });
                return;
            }

            await ctx.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, result });
        });
    }

    private static object HandleToolCall(JsonElement message, string? lastAuthHeader)
    {
        var prms = message.GetProperty("params");
        var name = prms.GetProperty("name").GetString();
        var args = prms.TryGetProperty("arguments", out var a) ? a : default;

        return name switch
        {
            "add" => TextContent(((args.GetProperty("a").GetDouble() + args.GetProperty("b").GetDouble())
                                 .ToString(System.Globalization.CultureInfo.InvariantCulture))),
            "echo" => TextContent(string.IsNullOrEmpty(lastAuthHeader)
                ? args.GetProperty("message").GetString() ?? ""
                : "saw header: " + lastAuthHeader),
            _ => new
            {
                content = new[] { new { type = "text", text = "unknown" } },
                isError = true
            }
        };
    }

    private static object TextContent(string text) => new
    {
        content = new[] { new { type = "text", text } }
    };
}
