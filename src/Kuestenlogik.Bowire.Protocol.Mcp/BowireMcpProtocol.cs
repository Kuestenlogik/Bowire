// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.Mcp;

/// <summary>
/// Bowire protocol plugin for the Model Context Protocol (MCP). Connects to
/// a remote MCP server, discovers its tools / resources / prompts, and lets
/// the user invoke them — analogous to the gRPC and REST plugins.
/// </summary>
/// <remarks>
/// The companion <see cref="McpAdapterEndpoints.MapBowireMcpAdapter"/>
/// extension lives in the same assembly but is unrelated to discovery: it is
/// an opt-in development feature that goes the other direction (Bowire's
/// services exposed as MCP tools so AI agents can call them).
/// </remarks>
// CA1001: _http lives for the lifetime of the protocol registry, which is
// the lifetime of the host process. Adding IDisposable to IBowireProtocol
// just to dispose a singleton at shutdown would ripple through every plugin
// without payoff.
#pragma warning disable CA1001
public sealed class BowireMcpProtocol : IBowireProtocol
#pragma warning restore CA1001
{
    // Built lazily from BowireHttpClientFactory in Initialize() so the
    // localhost-cert opt-in (Bowire:TrustLocalhostCert) reaches the
    // certificate validation callback. Falls back to a vanilla HttpClient
    // for test paths that skip Initialize.
    private HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    public string Name => "MCP";
    public string Id => "mcp";

    public void Initialize(IServiceProvider? serviceProvider)
    {
        var config = serviceProvider?.GetService<IConfiguration>();
        _http = BowireHttpClientFactory.Create(config, Id, TimeSpan.FromSeconds(30));
    }

    // Model Context Protocol — official three-stroke mark (modelcontextprotocol.io).
    public string IconSvg => """<svg viewBox="0 0 180 180" fill="none" stroke="currentColor" stroke-width="14" stroke-linecap="round" width="16" height="16" aria-hidden="true"><path d="M18 84.8528L85.8822 16.9706C95.2548 7.59798 110.451 7.59798 119.823 16.9706C129.196 26.3431 129.196 41.5391 119.823 50.9117L68.5581 102.177"/><path d="M69.2652 101.47L119.823 50.9117C129.196 41.5391 144.392 41.5391 153.765 50.9117L154.118 51.2652C163.491 60.6378 163.491 75.8338 154.118 85.2063L92.7248 146.6C89.6006 149.724 89.6006 154.789 92.7248 157.913L105.331 170.52"/><path d="M102.853 33.9411L52.6482 84.1457C43.2756 93.5183 43.2756 108.714 52.6482 118.087C62.0208 127.459 77.2167 127.459 86.5893 118.087L136.794 67.8822"/></svg>""";

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            return [];

        var endpoint = ResolveEndpoint(serverUrl);
        var client = new McpDiscoveryClient(_http, endpoint);

        try
        {
            await client.InitializeAsync(ct);
        }
        catch
        {
            // Server is not an MCP endpoint or rejected the handshake -- nothing to discover
            return [];
        }

        var services = new List<BowireServiceInfo>();

        // Tools
        try
        {
            var toolsResult = await client.ListToolsAsync(ct);
            var tools = ExtractTools(toolsResult);
            if (tools.Count > 0)
            {
                services.Add(new BowireServiceInfo("Tools", "mcp", tools)
                {
                    Source = "mcp",
                    OriginUrl = serverUrl,
                    Description = "MCP tools — invoke with the same form-based UI as gRPC unary methods."
                });
            }
        }
        catch { /* server may not support tools/list */ }

        // Resources
        try
        {
            var resourcesResult = await client.ListResourcesAsync(ct);
            var resources = ExtractResources(resourcesResult);
            if (resources.Count > 0)
            {
                services.Add(new BowireServiceInfo("Resources", "mcp", resources)
                {
                    Source = "mcp",
                    OriginUrl = serverUrl,
                    Description = "MCP resources — read by URI."
                });
            }
        }
        catch { /* server may not support resources/list */ }

        // Prompts
        try
        {
            var promptsResult = await client.ListPromptsAsync(ct);
            var prompts = ExtractPrompts(promptsResult);
            if (prompts.Count > 0)
            {
                services.Add(new BowireServiceInfo("Prompts", "mcp", prompts)
                {
                    Source = "mcp",
                    OriginUrl = serverUrl,
                    Description = "MCP prompts — render templated prompts with arguments."
                });
            }
        }
        catch { /* server may not support prompts/list */ }

        return services;
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var endpoint = ResolveEndpoint(serverUrl);
        // Forward auth / metadata headers to the MCP server. The auth-helper
        // pipeline (Bearer / Basic / API-Key / JWT / OAuth) populates the
        // metadata dict before we get here, and the client attaches every
        // entry as an HTTP header on every JSON-RPC request it sends.
        var client = new McpDiscoveryClient(_http, endpoint, metadata);

        var startedAt = DateTime.UtcNow;
        try
        {
            await client.InitializeAsync(ct);

            var arguments = ParseArguments(jsonMessages);

            JsonElement result = service switch
            {
                "Tools" => await client.CallToolAsync(method, arguments, ct),
                "Resources" => await client.ReadResourceAsync(method, ct),
                "Prompts" => await client.GetPromptAsync(method, arguments, ct),
                _ => throw new InvalidOperationException(
                    $"MCP service '{service}' is not supported. Use 'Tools', 'Resources', or 'Prompts'.")
            };

            var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            var json = JsonSerializer.Serialize(result, s_indented);
            return new InvokeResult(json, elapsedMs, "OK", new Dictionary<string, string>());
        }
        catch (Exception ex)
        {
            var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            return new InvokeResult(null, elapsedMs, ex.Message, new Dictionary<string, string>());
        }
    }

    public IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        // MCP tool calls are unary in the streamable HTTP transport. Streaming
        // notifications would require subscribing to the SSE transport, which
        // we'll add together with the SSE plugin integration.
        return AsyncEnumerable.Empty<string>();
    }

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => Task.FromResult<IBowireChannel?>(null);

    private static string ResolveEndpoint(string serverUrl)
    {
        // Accept either a bare base URL or a full path. We don't probe for
        // /mcp or /sse — the user supplies the actual endpoint URL when they
        // know the server's transport. The Bowire adapter's message endpoint
        // is /bowire/mcp/message, for example.
        return serverUrl.TrimEnd('/');
    }

    private static JsonElement ParseArguments(List<string> jsonMessages)
    {
        if (jsonMessages.Count == 0 || string.IsNullOrWhiteSpace(jsonMessages[0]))
            return default;

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(jsonMessages[0]);
        }
        catch
        {
            return default;
        }
    }

    private static List<BowireMethodInfo> ExtractTools(JsonElement result)
    {
        var methods = new List<BowireMethodInfo>();
        if (!result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            return methods;

        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool";
            var description = tool.TryGetProperty("description", out var d) ? d.GetString() : null;

            BowireMessageInfo input;
            if (tool.TryGetProperty("inputSchema", out var schema))
                input = McpDiscoveryClient.MapInputSchema(name, schema);
            else
                input = new BowireMessageInfo(name + "Input", name + "Input", []);

            var output = new BowireMessageInfo("ToolResult", "mcp.ToolResult", []);

            methods.Add(new BowireMethodInfo(
                Name: name,
                FullName: "Tools/" + name,
                ClientStreaming: false,
                ServerStreaming: false,
                InputType: input,
                OutputType: output,
                MethodType: "Unary")
            {
                Summary = description,
                Description = description
            });
        }

        return methods;
    }

    private static List<BowireMethodInfo> ExtractResources(JsonElement result)
    {
        var methods = new List<BowireMethodInfo>();
        if (!result.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
            return methods;

        foreach (var resource in resources.EnumerateArray())
        {
            var uri = resource.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
            var name = resource.TryGetProperty("name", out var n) ? n.GetString() ?? uri : uri;
            var description = resource.TryGetProperty("description", out var d) ? d.GetString() : null;

            // Resources are addressed by URI; the method name IS the URI so InvokeAsync can route it.
            var input = new BowireMessageInfo("ResourceRead", "mcp.ResourceRead", []);
            var output = new BowireMessageInfo("ResourceContent", "mcp.ResourceContent", []);

            var summary = string.IsNullOrEmpty(name) ? uri : $"{name} — {uri}";

            methods.Add(new BowireMethodInfo(
                Name: uri,
                FullName: "Resources/" + uri,
                ClientStreaming: false,
                ServerStreaming: false,
                InputType: input,
                OutputType: output,
                MethodType: "Unary")
            {
                Summary = summary,
                Description = description
            });
        }

        return methods;
    }

    private static List<BowireMethodInfo> ExtractPrompts(JsonElement result)
    {
        var methods = new List<BowireMethodInfo>();
        if (!result.TryGetProperty("prompts", out var prompts) || prompts.ValueKind != JsonValueKind.Array)
            return methods;

        foreach (var prompt in prompts.EnumerateArray())
        {
            var name = prompt.TryGetProperty("name", out var n) ? n.GetString() ?? "prompt" : "prompt";
            var description = prompt.TryGetProperty("description", out var d) ? d.GetString() : null;

            var input = BuildPromptInput(name, prompt);
            var output = new BowireMessageInfo("PromptResult", "mcp.PromptResult", []);

            methods.Add(new BowireMethodInfo(
                Name: name,
                FullName: "Prompts/" + name,
                ClientStreaming: false,
                ServerStreaming: false,
                InputType: input,
                OutputType: output,
                MethodType: "Unary")
            {
                Summary = description,
                Description = description
            });
        }

        return methods;
    }

    private static BowireMessageInfo BuildPromptInput(string promptName, JsonElement prompt)
    {
        var fields = new List<BowireFieldInfo>();
        if (!prompt.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Array)
            return new BowireMessageInfo(promptName + "Input", promptName + "Input", fields);

        var i = 1;
        foreach (var arg in args.EnumerateArray())
        {
            var argName = arg.TryGetProperty("name", out var n) ? n.GetString() ?? $"arg{i}" : $"arg{i}";
            var argDesc = arg.TryGetProperty("description", out var d) ? d.GetString() : null;
            var required = arg.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;

            fields.Add(new BowireFieldInfo(
                Name: argName,
                Number: i++,
                Type: "string",
                Label: required ? "required" : "optional",
                IsMap: false,
                IsRepeated: false,
                MessageType: null,
                EnumValues: null)
            {
                Required = required,
                Description = argDesc,
                Source = "body"
            });
        }

        return new BowireMessageInfo(promptName + "Input", promptName + "Input", fields);
    }
}
