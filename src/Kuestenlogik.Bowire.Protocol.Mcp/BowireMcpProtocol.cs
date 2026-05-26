// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Protocol.Mcp;

/// <summary>
/// Bowire protocol plugin for the Model Context Protocol (MCP). Connects to
/// a remote MCP server, discovers its tools / resources / prompts, and lets
/// the user invoke them — analogous to the gRPC and REST plugins.
/// </summary>
/// <remarks>
/// <para>
/// Built on the official <c>ModelContextProtocol</c> C# SDK
/// (<see cref="McpClient"/> + <see cref="HttpClientTransport"/>) — the
/// previous hand-rolled JSON-RPC client predates the SDK's client
/// surface. The SDK handles spec-version negotiation, Streamable-HTTP-
/// vs-SSE auto-detection, session resumption, and the JSON-RPC wire
/// envelope.
/// </para>
/// <para>
/// The companion <see cref="McpAdapterEndpoints.MapBowireMcpAdapter"/>
/// extension lives in the same assembly but is unrelated to discovery: it is
/// an opt-in development feature that goes the other direction (Bowire's
/// services exposed as MCP tools so AI agents can call them). The
/// adapter is hand-rolled because it sits on Bowire's own
/// <c>BowireProtocolRegistry</c> rather than ASP.NET MCP middleware.
/// </para>
/// </remarks>
public sealed class BowireMcpProtocol : IBowireProtocol
{
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    public string Name => "MCP";
    public string Id => "mcp";

    // Initialize stays a no-op: the SDK owns its own HttpClient through
    // HttpClientTransportOptions, and the localhost-cert opt-in we used
    // to thread through BowireHttpClientFactory isn't reachable from
    // the SDK transport. Embedded hosts that need a custom HttpClient
    // can subclass the plugin or wait for the SDK to expose the seam.
    public void Initialize(IServiceProvider? serviceProvider) { }

    // Model Context Protocol — official three-stroke mark (modelcontextprotocol.io).
    public string IconSvg => """<svg viewBox="0 0 180 180" fill="none" stroke="currentColor" stroke-width="14" stroke-linecap="round" width="16" height="16" aria-hidden="true"><path d="M18 84.8528L85.8822 16.9706C95.2548 7.59798 110.451 7.59798 119.823 16.9706C129.196 26.3431 129.196 41.5391 119.823 50.9117L68.5581 102.177"/><path d="M69.2652 101.47L119.823 50.9117C129.196 41.5391 144.392 41.5391 153.765 50.9117L154.118 51.2652C163.491 60.6378 163.491 75.8338 154.118 85.2063L92.7248 146.6C89.6006 149.724 89.6006 154.789 92.7248 157.913L105.331 170.52"/><path d="M102.853 33.9411L52.6482 84.1457C43.2756 93.5183 43.2756 108.714 52.6482 118.087C62.0208 127.459 77.2167 127.459 86.5893 118.087L136.794 67.8822"/></svg>""";

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            return [];

        McpClient? client = null;
        try
        {
            client = await CreateClientAsync(serverUrl, metadata: null, ct).ConfigureAwait(false);
        }
        catch
        {
            // Server is not an MCP endpoint or rejected the handshake — nothing to discover.
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            return [];
        }

        await using (client)
        {
            var services = new List<BowireServiceInfo>();

            // Tools, resources, and prompts are all optional on the
            // server side — the SDK throws NotSupportedException when
            // the capability is absent; treat that as "no entries".
            await AddToolsAsync(client, services, serverUrl, ct).ConfigureAwait(false);
            await AddResourcesAsync(client, services, serverUrl, ct).ConfigureAwait(false);
            await AddPromptsAsync(client, services, serverUrl, ct).ConfigureAwait(false);

            return services;
        }
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        McpClient? client = null;
        try
        {
            client = await CreateClientAsync(serverUrl, metadata, ct).ConfigureAwait(false);

            await using (client)
            {
                JsonElement payload = service switch
                {
                    "Tools" => await CallToolAsync(client, method, jsonMessages, ct).ConfigureAwait(false),
                    "Resources" => await ReadResourceAsync(client, method, ct).ConfigureAwait(false),
                    "Prompts" => await GetPromptAsync(client, method, jsonMessages, ct).ConfigureAwait(false),
                    _ => throw new InvalidOperationException(
                        $"MCP service '{service}' is not supported. Use 'Tools', 'Resources', or 'Prompts'."),
                };

                var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                var json = JsonSerializer.Serialize(payload, s_indented);
                return new InvokeResult(json, elapsedMs, "OK", new Dictionary<string, string>());
            }
        }
        catch (Exception ex)
        {
            var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            return new InvokeResult(null, elapsedMs, ex.Message, new Dictionary<string, string>());
        }
    }

    public IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        // MCP tool calls are unary in the request/response sense. Server
        // notifications (progress, log entries) are a separate seam the
        // SDK exposes via handlers — when we wire them through Bowire's
        // streaming surface this method will fan them out.
        return AsyncEnumerable.Empty<string>();
    }

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => Task.FromResult<IBowireChannel?>(null);

    // ----- SDK plumbing -------------------------------------------------

    private static async Task<McpClient> CreateClientAsync(
        string serverUrl,
        Dictionary<string, string>? metadata,
        CancellationToken ct)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(serverUrl.TrimEnd('/'), UriKind.Absolute),
            // AutoDetect lets the SDK try Streamable HTTP first and fall
            // back to SSE+POST (the 2024-11-05 transport) when the
            // server returns 405 or text/event-stream content type on
            // the initialize POST. Same behaviour we had hand-rolled.
            TransportMode = HttpTransportMode.AutoDetect,
            AdditionalHeaders = metadata is null
                ? null
                : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase),
        };

        // McpClient.CreateAsync takes ownership of the transport on
        // success (closing the McpClient closes the transport too).
        // On a throw before hand-off we dispose explicitly via the
        // async path; CA2000 sees IAsyncDisposable but doesn't track
        // it across awaits, so silence it here.
#pragma warning disable CA2000
        var transport = new HttpClientTransport(options);
#pragma warning restore CA2000
        try
        {
            return await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ----- discovery: tools / resources / prompts -----------------------

    private static async Task AddToolsAsync(
        McpClient client, List<BowireServiceInfo> services, string serverUrl, CancellationToken ct)
    {
        IList<McpClientTool> tools;
        try { tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false); }
        catch { return; }
        if (tools.Count == 0) return;

        var methods = new List<BowireMethodInfo>(tools.Count);
        foreach (var tool in tools)
        {
            methods.Add(new BowireMethodInfo(
                Name: tool.Name,
                FullName: "Tools/" + tool.Name,
                ClientStreaming: false,
                ServerStreaming: false,
                InputType: MapToolInputSchema(tool),
                OutputType: new BowireMessageInfo("ToolResult", "mcp.ToolResult", []),
                MethodType: "Unary")
            {
                Summary = tool.Description,
                Description = tool.Description,
            });
        }

        services.Add(new BowireServiceInfo("Tools", "mcp", methods)
        {
            Source = "mcp",
            OriginUrl = serverUrl,
            Description = "MCP tools — invoke with the same form-based UI as gRPC unary methods.",
        });
    }

    private static async Task AddResourcesAsync(
        McpClient client, List<BowireServiceInfo> services, string serverUrl, CancellationToken ct)
    {
        IList<McpClientResource> resources;
        try { resources = await client.ListResourcesAsync(cancellationToken: ct).ConfigureAwait(false); }
        catch { return; }
        if (resources.Count == 0) return;

        var methods = new List<BowireMethodInfo>(resources.Count);
        foreach (var res in resources)
        {
            var summary = string.IsNullOrEmpty(res.Name) ? res.Uri : $"{res.Name} — {res.Uri}";
            methods.Add(new BowireMethodInfo(
                Name: res.Uri,
                FullName: "Resources/" + res.Uri,
                ClientStreaming: false,
                ServerStreaming: false,
                InputType: new BowireMessageInfo("ResourceRead", "mcp.ResourceRead", []),
                OutputType: new BowireMessageInfo("ResourceContent", "mcp.ResourceContent", []),
                MethodType: "Unary")
            {
                Summary = summary,
                Description = res.Description,
            });
        }

        services.Add(new BowireServiceInfo("Resources", "mcp", methods)
        {
            Source = "mcp",
            OriginUrl = serverUrl,
            Description = "MCP resources — read by URI.",
        });
    }

    private static async Task AddPromptsAsync(
        McpClient client, List<BowireServiceInfo> services, string serverUrl, CancellationToken ct)
    {
        IList<McpClientPrompt> prompts;
        try { prompts = await client.ListPromptsAsync(cancellationToken: ct).ConfigureAwait(false); }
        catch { return; }
        if (prompts.Count == 0) return;

        var methods = new List<BowireMethodInfo>(prompts.Count);
        foreach (var prompt in prompts)
        {
            methods.Add(new BowireMethodInfo(
                Name: prompt.Name,
                FullName: "Prompts/" + prompt.Name,
                ClientStreaming: false,
                ServerStreaming: false,
                InputType: BuildPromptInput(prompt),
                OutputType: new BowireMessageInfo("PromptResult", "mcp.PromptResult", []),
                MethodType: "Unary")
            {
                Summary = prompt.Description,
                Description = prompt.Description,
            });
        }

        services.Add(new BowireServiceInfo("Prompts", "mcp", methods)
        {
            Source = "mcp",
            OriginUrl = serverUrl,
            Description = "MCP prompts — render templated prompts with arguments.",
        });
    }

    // ----- invocation ---------------------------------------------------

    private static async Task<JsonElement> CallToolAsync(
        McpClient client, string toolName, List<string> jsonMessages, CancellationToken ct)
    {
        var args = ParseArguments(jsonMessages);
        var result = await client.CallToolAsync(toolName, args, cancellationToken: ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result);
    }

    private static async Task<JsonElement> ReadResourceAsync(
        McpClient client, string uri, CancellationToken ct)
    {
        var result = await client
            .ReadResourceAsync(new Uri(uri, UriKind.RelativeOrAbsolute), cancellationToken: ct)
            .ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result);
    }

    private static async Task<JsonElement> GetPromptAsync(
        McpClient client, string name, List<string> jsonMessages, CancellationToken ct)
    {
        var args = ParseArguments(jsonMessages)
            ?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        var result = await client.GetPromptAsync(name, args, cancellationToken: ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result);
    }

    private static Dictionary<string, object?>? ParseArguments(List<string> jsonMessages)
    {
        if (jsonMessages.Count == 0 || string.IsNullOrWhiteSpace(jsonMessages[0]))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(jsonMessages[0]);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.Clone(),
                };
            }
            return dict;
        }
        catch
        {
            return null;
        }
    }

    // ----- schema mapping -----------------------------------------------

    private static BowireMessageInfo MapToolInputSchema(McpClientTool tool)
    {
        // McpClientTool.JsonSchema is the same JSON-Schema-object MCP
        // ships in tools/list responses. Walk top-level properties +
        // required[] into BowireFieldInfos so Bowire's form UI can render
        // it the same way it does gRPC inputs.
        var fields = new List<BowireFieldInfo>();
        var schema = tool.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object)
            return new BowireMessageInfo(tool.Name + "Input", tool.Name + "Input", fields);

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in req.EnumerateArray())
            {
                var name = item.GetString();
                if (!string.IsNullOrEmpty(name)) required.Add(name);
            }
        }

        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            var i = 1;
            foreach (var prop in props.EnumerateObject())
            {
                var type = prop.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "string" : "string";
                var description = prop.Value.TryGetProperty("description", out var d) ? d.GetString() : null;
                var isRequired = required.Contains(prop.Name);

                fields.Add(new BowireFieldInfo(
                    Name: prop.Name,
                    Number: i++,
                    Type: type,
                    Label: isRequired ? "required" : "optional",
                    IsMap: false,
                    IsRepeated: type == "array",
                    MessageType: null,
                    EnumValues: null)
                {
                    Required = isRequired,
                    Description = description,
                    Source = "body",
                });
            }
        }

        return new BowireMessageInfo(tool.Name + "Input", tool.Name + "Input", fields);
    }

    private static BowireMessageInfo BuildPromptInput(McpClientPrompt prompt)
    {
        var fields = new List<BowireFieldInfo>();
        var args = prompt.ProtocolPrompt.Arguments;
        if (args is null || args.Count == 0)
            return new BowireMessageInfo(prompt.Name + "Input", prompt.Name + "Input", fields);

        var i = 1;
        foreach (var arg in args)
        {
            var argRequired = arg.Required ?? false;
            fields.Add(new BowireFieldInfo(
                Name: arg.Name,
                Number: i++,
                Type: "string",
                Label: argRequired ? "required" : "optional",
                IsMap: false,
                IsRepeated: false,
                MessageType: null,
                EnumValues: null)
            {
                Required = argRequired,
                Description = arg.Description,
                Source = "body",
            });
        }

        return new BowireMessageInfo(prompt.Name + "Input", prompt.Name + "Input", fields);
    }
}
