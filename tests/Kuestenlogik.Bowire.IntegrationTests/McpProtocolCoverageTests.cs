// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Coverage-targeted end-to-end tests for the MCP plugin: drives the
/// adapter HTTP endpoint, the SSE response transport in
/// <see cref="McpDiscoveryClient"/>, the resources/prompts read paths in
/// <see cref="BowireMcpProtocol"/>, and the per-list catch blocks that
/// keep partial discovery alive when a server only supports some MCP
/// list verbs.
/// </summary>
public sealed class McpProtocolCoverageTests
{
    [Fact]
    public async Task AdapterEndpoint_Initialize_Returns_Server_Capabilities()
    {
        await using var host = await PluginTestHost.StartAsync(MapAdapterOnly);
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await client.PostAsJsonAsync("/bowire/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize"
        }, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("2.0", body.GetProperty("jsonrpc").GetString());
        Assert.Equal("bowire", body.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task AdapterEndpoint_Notification_Returns_204_No_Content()
    {
        await using var host = await PluginTestHost.StartAsync(MapAdapterOnly);
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await client.PostAsJsonAsync("/bowire/mcp", new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AdapterEndpoint_Invalid_Json_Returns_400()
    {
        await using var host = await PluginTestHost.StartAsync(MapAdapterOnly);
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        using var content = new StringContent("{not-json}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync(new Uri("/bowire/mcp", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Invalid JSON", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WithMcpAdapter_Defaults_Map_Endpoint_And_Initialize_Reachable()
    {
        // Exercises the WithMcpAdapter() overload: it discovers loaded
        // protocols from the running test host and wires the same /bowire/mcp
        // route. The default URL fallback ("http://localhost") and the
        // protocol-init loop both sit inside this overload.
        await using var host = await PluginTestHost.StartAsync(app => app.WithMcpAdapter());

        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };
        var response = await client.PostAsJsonAsync("/bowire/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "ping"
        }, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Object, body.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task BowireMcpProtocol_Reads_Resource_By_Uri()
    {
        // Hits BowireMcpProtocol.InvokeAsync on the "Resources" arm and
        // McpDiscoveryClient.ReadResourceAsync.
        await using var host = await PluginTestHost.StartAsync(MapMcpServer);
        var protocol = new BowireMcpProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/mcp",
            service: "Resources",
            method: "notes://welcome",
            jsonMessages: [],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("first note body", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BowireMcpProtocol_Renders_Prompt_With_Arguments()
    {
        // Hits the "Prompts" arm in InvokeAsync + McpDiscoveryClient.GetPromptAsync.
        await using var host = await PluginTestHost.StartAsync(MapMcpServer);
        var protocol = new BowireMcpProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/mcp",
            service: "Prompts",
            method: "greet",
            jsonMessages: ["{\"name\":\"Ada\"}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("Hello Ada", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BowireMcpProtocol_Unknown_Service_Returns_Error_Status()
    {
        await using var host = await PluginTestHost.StartAsync(MapMcpServer);
        var protocol = new BowireMcpProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/mcp",
            service: "NotAThing",
            method: "x",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
        Assert.Contains("NotAThing", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BowireMcpProtocol_Discover_Survives_Per_List_Failures()
    {
        // The fake server only answers tools/list; resources/list and
        // prompts/list throw a JSON-RPC error -> their catch blocks must
        // swallow and the discover call still returns the Tools service.
        await using var host = await PluginTestHost.StartAsync(MapPartialMcpServer);
        var protocol = new BowireMcpProtocol();

        var services = await protocol.DiscoverAsync(
            host.BaseUrl + "/mcp", showInternalServices: false, TestContext.Current.CancellationToken);

        var service = Assert.Single(services);
        Assert.Equal("Tools", service.Name);
    }

    [Fact]
    public async Task BowireMcpProtocol_Discover_Tools_Failure_Caught_And_Other_Lists_Still_Run()
    {
        // tools/list returns a JSON-RPC error -> the per-list catch swallows
        // it, but resources/list still succeeds, so we get a Resources service.
        await using var host = await PluginTestHost.StartAsync(MapToolsBrokenServer);
        var protocol = new BowireMcpProtocol();

        var services = await protocol.DiscoverAsync(
            host.BaseUrl + "/mcp", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Contains(services, s => s.Name == "Resources");
        Assert.DoesNotContain(services, s => s.Name == "Tools");
    }

    [Fact]
    public async Task BowireMcpProtocol_Discover_Returns_Empty_When_Notification_Fails()
    {
        // The server returns 500 on notifications/initialized -> the
        // notification throws inside InitializeAsync, the outer catch in
        // DiscoverAsync swallows it, and we get an empty service list.
        await using var host = await PluginTestHost.StartAsync(MapServerThatRejectsNotification);
        var protocol = new BowireMcpProtocol();

        var services = await protocol.DiscoverAsync(
            host.BaseUrl + "/mcp", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task BowireMcpProtocol_Reads_Sse_Framed_Response()
    {
        // Server replies with text/event-stream; McpDiscoveryClient must
        // detect the content type and switch to ReadSseResponseAsync.
        await using var host = await PluginTestHost.StartAsync(MapSseMcpServer);
        var protocol = new BowireMcpProtocol();

        var services = await protocol.DiscoverAsync(
            host.BaseUrl + "/mcp", showInternalServices: false, TestContext.Current.CancellationToken);

        var tools = services.Single(s => s.Name == "Tools");
        Assert.Equal("ping", tools.Methods.Single().Name);
    }

    private static void MapAdapterOnly(WebApplication app)
    {
        // The adapter alone -- no upstream protocols, no discovery surface.
        app.MapBowireMcpAdapter(new BowireProtocolRegistry(), "http://localhost");
    }

    private static void MapMcpServer(WebApplication app)
    {
        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            var message = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = message.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = message.TryGetProperty("id", out var idProp) ? (JsonElement?)idProp : null;

            object? result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    serverInfo = new { name = "fake", version = "0.1" }
                },
                "notifications/initialized" => null,
                "tools/list" => new { tools = Array.Empty<object>() },
                "resources/list" => new
                {
                    resources = new object[]
                    {
                        new { uri = "notes://welcome", name = "Welcome" }
                    }
                },
                "resources/read" => new
                {
                    contents = new object[]
                    {
                        new
                        {
                            uri = message.GetProperty("params").GetProperty("uri").GetString(),
                            mimeType = "text/plain",
                            text = "first note body"
                        }
                    }
                },
                "prompts/list" => new
                {
                    prompts = new object[]
                    {
                        new
                        {
                            name = "greet",
                            arguments = new object[] { new { name = "name", required = true } }
                        }
                    }
                },
                "prompts/get" => new
                {
                    messages = new object[]
                    {
                        new
                        {
                            role = "assistant",
                            content = new
                            {
                                type = "text",
                                text = "Hello " + message.GetProperty("params").GetProperty("arguments").GetProperty("name").GetString()
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

    private static void MapPartialMcpServer(WebApplication app)
    {
        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            var message = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = message.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = message.TryGetProperty("id", out var idProp) ? (JsonElement?)idProp : null;

            if (method == "notifications/initialized")
            {
                ctx.Response.StatusCode = 204;
                return;
            }

            if (method == "initialize")
            {
                await ctx.Response.WriteAsJsonAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new { protocolVersion = "2024-11-05", capabilities = new { }, serverInfo = new { name = "p", version = "0" } }
                });
                return;
            }

            if (method == "tools/list")
            {
                await ctx.Response.WriteAsJsonAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new { tools = new object[] { new { name = "noop" } } }
                });
                return;
            }

            // resources/list and prompts/list both fail with a JSON-RPC error.
            await ctx.Response.WriteAsJsonAsync(new
            {
                jsonrpc = "2.0",
                id,
                error = new { code = -32601, message = $"Not supported: {method}" }
            });
        });
    }

    private static void MapSseMcpServer(WebApplication app)
    {
        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            var message = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = message.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = message.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;

            if (method == "notifications/initialized")
            {
                ctx.Response.StatusCode = 204;
                return;
            }

            ctx.Response.ContentType = "text/event-stream";

            object payload = method switch
            {
                "initialize" => new { protocolVersion = "2024-11-05", capabilities = new { }, serverInfo = new { name = "sse", version = "0" } },
                "tools/list" => new { tools = new object[] { new { name = "ping" } } },
                _ => new { }
            };

            var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = payload });
            // Emit a noisy frame first to exercise the id-mismatch skip path.
            await ctx.Response.WriteAsync($"event: message\ndata: {{\"jsonrpc\":\"2.0\",\"id\":-99,\"result\":{{}}}}\n\n");
            await ctx.Response.WriteAsync($"event: message\ndata: {json}\n\n");
            await ctx.Response.Body.FlushAsync();
        });
    }

    private static void MapToolsBrokenServer(WebApplication app)
    {
        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            var message = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = message.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = message.TryGetProperty("id", out var idProp) ? (JsonElement?)idProp : null;

            if (method == "notifications/initialized")
            {
                ctx.Response.StatusCode = 204;
                return;
            }

            if (method == "tools/list")
            {
                await ctx.Response.WriteAsJsonAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32601, message = "tools/list disabled" }
                });
                return;
            }

            object? result = method switch
            {
                "initialize" => new { protocolVersion = "2024-11-05", capabilities = new { }, serverInfo = new { name = "x", version = "0" } },
                "resources/list" => new { resources = new object[] { new { uri = "x://y", name = "Y" } } },
                "prompts/list" => new { prompts = Array.Empty<object>() },
                _ => null
            };

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

    private static void MapServerThatRejectsNotification(WebApplication app)
    {
        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            var message = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = message.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = message.TryGetProperty("id", out var idProp) ? (JsonElement?)idProp : null;

            if (method == "notifications/initialized")
            {
                // Force the SendNotificationAsync EnsureSuccessStatusCode branch.
                ctx.Response.StatusCode = 500;
                return;
            }

            if (method == "initialize")
            {
                await ctx.Response.WriteAsJsonAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new { protocolVersion = "2024-11-05", capabilities = new { }, serverInfo = new { name = "n", version = "0" } }
                });
                return;
            }

            ctx.Response.StatusCode = 500;
        });
    }
}
