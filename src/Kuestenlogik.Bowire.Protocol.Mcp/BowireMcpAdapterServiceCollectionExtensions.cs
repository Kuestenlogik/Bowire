// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Protocol.Mcp;

/// <summary>
/// DI registration for the Bowire MCP adapter — the server side that
/// exposes Bowire's discovered API services as MCP tools, resources,
/// and prompts so AI agents (Claude, Copilot, Cursor) can call them.
/// </summary>
/// <remarks>
/// <para>
/// Built on the official <c>ModelContextProtocol.AspNetCore</c> SDK:
/// the previous hand-rolled JSON-RPC pipeline
/// (<c>McpAdapterServer.HandleMessageAsync</c>) is replaced by the
/// SDK's <c>AddMcpServer().WithHttpTransport()</c> chain, and tools /
/// resources / prompts are injected as dynamic handlers because
/// Bowire's surface depends on what was discovered at runtime, not
/// what's known at compile time.
/// </para>
/// <para>
/// Usage:
/// </para>
/// <code>
/// builder.Services.AddBowireMcpAdapter("http://localhost:5005");
/// var app = builder.Build();
/// app.MapBowire(opts =&gt; opts.Title = "My API");
/// app.MapBowireMcpAdapter("/bowire");
/// </code>
/// </remarks>
public static class BowireMcpAdapterServiceCollectionExtensions
{
    /// <summary>
    /// DI key that <see cref="AddBowireMcpAdapter"/> stashes the
    /// target server URL under so the runtime handlers (which only
    /// see <c>IServiceProvider</c> at request time) can read it back.
    /// </summary>
    internal const string ServerUrlServiceKey = "Kuestenlogik.Bowire.Mcp.AdapterServerUrl";

    /// <summary>
    /// Register the Bowire MCP adapter with the host's service
    /// collection. Pair with
    /// <see cref="McpAdapterEndpoints.MapBowireMcpAdapter"/> at
    /// map-time to actually mount the endpoint.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="serverUrl">
    /// The target server URL the adapter should run discovery and
    /// invoke against. Optional; defaults to <c>http://localhost</c>
    /// when omitted, matching the previous behaviour of
    /// <c>WithMcpAdapter()</c> without an argument.
    /// </param>
    public static IServiceCollection AddBowireMcpAdapter(
        this IServiceCollection services,
        string? serverUrl = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var resolvedUrl = serverUrl ?? "http://localhost";
        services.AddKeyedSingleton(ServerUrlServiceKey, resolvedUrl);

        services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation
                {
                    Name = "bowire",
                    Version = typeof(BowireMcpAdapterServiceCollectionExtensions).Assembly
                        .GetName().Version?.ToString() ?? "0.0.0",
                    Title = "Bowire workbench (adapter)",
                };
            })
            .WithHttpTransport()
            .WithListToolsHandler(HandleListToolsAsync)
            .WithCallToolHandler(HandleCallToolAsync)
            .WithListResourcesHandler(HandleListResourcesAsync)
            .WithReadResourceHandler(HandleReadResourceAsync)
            .WithListPromptsHandler(HandleListPromptsAsync)
            .WithGetPromptHandler(HandleGetPromptAsync);

        return services;
    }

    // ----- handler implementations -------------------------------------

    private static async ValueTask<ListToolsResult> HandleListToolsAsync(
        RequestContext<ListToolsRequestParams> ctx, CancellationToken ct)
    {
        var (registry, serverUrl) = ResolveRegistryAndUrl(ctx);
        var tools = new List<Tool>();

        foreach (var protocol in registry.Protocols)
        {
            if (string.Equals(protocol.Id, "mcp", StringComparison.OrdinalIgnoreCase)) continue;

            List<Models.BowireServiceInfo> services;
            try { services = await protocol.DiscoverAsync(serverUrl, showInternalServices: false, ct).ConfigureAwait(false); }
            catch { continue; }

            foreach (var service in services)
            {
                foreach (var method in service.Methods)
                {
                    // Only unary methods become tools. Streaming methods
                    // need the channel surface, which doesn't translate
                    // to a single MCP tool-call.
                    if (!string.Equals(method.MethodType, "Unary", StringComparison.Ordinal)) continue;

                    var toolName = $"{service.Name.Replace('.', '_')}_{method.Name}";
                    var description = $"{method.MethodType} {protocol.Name} method: {service.Name}/{method.Name}";

                    tools.Add(new Tool
                    {
                        Name = toolName,
                        Description = description,
                        // Tool.InputSchema is a JsonElement; the SDK
                        // serialises the JsonObject we build below and
                        // hands the resulting JsonElement back here.
                        InputSchema = JsonSerializer.SerializeToElement(BuildInputSchema(method)),
                    });
                }
            }
        }

        return new ListToolsResult { Tools = tools };
    }

    private static async ValueTask<CallToolResult> HandleCallToolAsync(
        RequestContext<CallToolRequestParams> ctx, CancellationToken ct)
    {
        var (registry, serverUrl) = ResolveRegistryAndUrl(ctx);
        var toolName = ctx.Params?.Name ?? string.Empty;
        var arguments = ctx.Params?.Arguments;

        foreach (var protocol in registry.Protocols)
        {
            if (string.Equals(protocol.Id, "mcp", StringComparison.OrdinalIgnoreCase)) continue;

            List<Models.BowireServiceInfo> services;
            try { services = await protocol.DiscoverAsync(serverUrl, showInternalServices: false, ct).ConfigureAwait(false); }
            catch { continue; }

            foreach (var service in services)
            {
                foreach (var method in service.Methods)
                {
                    var candidateName = $"{service.Name.Replace('.', '_')}_{method.Name}";
                    if (!string.Equals(candidateName, toolName, StringComparison.Ordinal)) continue;

                    try
                    {
                        var jsonBody = arguments is null
                            ? "{}"
                            : JsonSerializer.Serialize(arguments);
                        var result = await protocol.InvokeAsync(
                            serverUrl, service.Name, method.Name,
                            [jsonBody], showInternalServices: false,
                            metadata: null, ct: ct).ConfigureAwait(false);

                        return new CallToolResult
                        {
                            Content =
                            [
                                new TextContentBlock { Text = result.Response ?? "null" },
                            ],
                        };
                    }
                    catch (Exception ex)
                    {
                        return new CallToolResult
                        {
                            Content =
                            [
                                new TextContentBlock { Text = $"Error: {ex.Message}" },
                            ],
                            IsError = true,
                        };
                    }
                }
            }
        }

        // SDK converts McpException to a JSON-RPC error envelope
        // automatically. We surface the cause in the message; the
        // SDK assigns the appropriate error code (-32603 internal).
        throw new McpException($"Tool not found: {toolName}");
    }

    private static async ValueTask<ListResourcesResult> HandleListResourcesAsync(
        RequestContext<ListResourcesRequestParams> ctx, CancellationToken ct)
    {
        var (registry, serverUrl) = ResolveRegistryAndUrl(ctx);
        var resources = new List<Resource>();

        foreach (var protocol in registry.Protocols)
        {
            if (string.Equals(protocol.Id, "mcp", StringComparison.OrdinalIgnoreCase)) continue;
            List<Models.BowireServiceInfo> services;
            try { services = await protocol.DiscoverAsync(serverUrl, showInternalServices: false, ct).ConfigureAwait(false); }
            catch { continue; }

            foreach (var service in services)
            {
                var uri = $"bowire-service://{protocol.Id}/{service.Name}";
                resources.Add(new Resource
                {
                    Uri = uri,
                    Name = service.Name,
                    Description = $"{protocol.Name} service schema — {service.Methods.Count} method(s).",
                    MimeType = "application/json",
                });
            }
        }

        return new ListResourcesResult { Resources = resources };
    }

    private static async ValueTask<ReadResourceResult> HandleReadResourceAsync(
        RequestContext<ReadResourceRequestParams> ctx, CancellationToken ct)
    {
        var (registry, serverUrl) = ResolveRegistryAndUrl(ctx);
        var uri = ctx.Params?.Uri ?? string.Empty;

        const string scheme = "bowire-service://";
        if (!uri.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            throw new McpException($"Unsupported resource URI: {uri}");

        var rest = uri[scheme.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
            throw new McpException(
                $"Malformed resource URI (expected bowire-service://<plugin>/<service>): {uri}");

        var pluginId = rest[..slash];
        var serviceName = rest[(slash + 1)..];

        var protocol = registry.Protocols.FirstOrDefault(
            p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (protocol is null)
            throw new McpException($"Unknown plugin id: {pluginId}");

        List<Models.BowireServiceInfo> services;
        try { services = await protocol.DiscoverAsync(serverUrl, showInternalServices: false, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            throw new McpException($"Discovery failed for {pluginId}: {ex.Message}", ex);
        }

        var service = services.FirstOrDefault(
            s => string.Equals(s.Name, serviceName, StringComparison.Ordinal));
        if (service is null)
            throw new McpException($"Unknown service: {serviceName}");

        var payload = JsonSerializer.Serialize(new
        {
            plugin = pluginId,
            service = service.Name,
            description = service.Description,
            methods = service.Methods.Select(m => new
            {
                name = m.Name,
                methodType = m.MethodType,
                inputType = m.InputType?.Name,
                inputFields = m.InputType?.Fields?.Select(f => new
                {
                    f.Name, f.Type, f.Number, f.Label, f.IsRepeated, f.IsMap,
                }),
                outputType = m.OutputType?.Name,
                outputFields = m.OutputType?.Fields?.Select(f => new
                {
                    f.Name, f.Type, f.Number, f.Label, f.IsRepeated, f.IsMap,
                }),
            }),
        }, s_jsonOpts);

        return new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = uri,
                    MimeType = "application/json",
                    Text = payload,
                },
            ],
        };
    }

    private static ValueTask<ListPromptsResult> HandleListPromptsAsync(
        RequestContext<ListPromptsRequestParams> ctx, CancellationToken ct)
    {
        var prompts = new List<Prompt>
        {
            new()
            {
                Name = "describe-service",
                Description = "Summarise what a discovered service does — method list, request/response shape, likely intent. Reads the service schema via the bowire-service:// resource.",
                Arguments =
                [
                    new PromptArgument
                    {
                        Name = "service",
                        Description = "Fully-qualified service name (e.g. weather.WeatherService).",
                        Required = true,
                    },
                ],
            },
            new()
            {
                Name = "generate-sample-request",
                Description = "Generate a valid JSON request body for a service method, ready to feed into a tools/call. Uses the field types from the service schema and picks plausible sample values.",
                Arguments =
                [
                    new PromptArgument
                    {
                        Name = "service",
                        Description = "Fully-qualified service name.",
                        Required = true,
                    },
                    new PromptArgument
                    {
                        Name = "method",
                        Description = "Method name on that service.",
                        Required = true,
                    },
                ],
            },
        };

        return ValueTask.FromResult(new ListPromptsResult { Prompts = prompts });
    }

    private static ValueTask<GetPromptResult> HandleGetPromptAsync(
        RequestContext<GetPromptRequestParams> ctx, CancellationToken ct)
    {
        var name = ctx.Params?.Name ?? string.Empty;
        var args = ctx.Params?.Arguments;

        string Arg(string key)
        {
            if (args is null || !args.TryGetValue(key, out var v))
                return "";
            return v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : v.GetRawText();
        }

        string text = name switch
        {
            "describe-service" => string.Join("\n", new[]
            {
                $"Summarise the service `{Arg("service")}` in three short paragraphs.",
                "",
                $"1. Read the resource `bowire-service://<plugin>/{Arg("service")}` to get the method list and field types. If you don't know the plugin id, list resources first.",
                "2. Identify the dominant pattern: CRUD? Query/Command? Streaming? Mention the top three methods by name.",
                "3. Suggest one likely use case the operator might want to test next, and the matching tools/call you would issue.",
            }),
            "generate-sample-request" => string.Join("\n", new[]
            {
                $"Generate a valid JSON request body for `{Arg("service")}/{Arg("method")}`, ready to feed into `tools/call`.",
                "",
                $"1. Read `bowire-service://<plugin>/{Arg("service")}` for the field schema (types, repeated/map flags, required fields).",
                "2. Produce values that are realistic, not just defaults — a `userId` should look like an id, a `timestamp` should be ISO-8601, a `lat`/`lon` pair should be a real place.",
                "3. Return exactly one JSON object — no prose, no markdown fences. Ready to paste into the tool's arguments field.",
            }),
            _ => $"Unknown prompt: {name}",
        };

        var description = name switch
        {
            "describe-service" => "Three-paragraph summary of a discovered service.",
            "generate-sample-request" => "JSON request body ready for tools/call.",
            _ => "Unknown prompt",
        };

        return ValueTask.FromResult(new GetPromptResult
        {
            Description = description,
            Messages =
            [
                new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContentBlock { Text = text },
                },
            ],
        });
    }

    // ----- helpers -----------------------------------------------------

    private static readonly JsonSerializerOptions s_jsonOpts = new() { WriteIndented = false };

    private static (BowireProtocolRegistry Registry, string ServerUrl) ResolveRegistryAndUrl<T>(
        RequestContext<T> ctx)
    {
        // The registry is cached on BowireApiEndpoints' static seam so we
        // don't pay the assembly-scan cost twice. If MapBowire ran before
        // us (the normal flow), GetRegistry returns the same instance the
        // workbench is using. If not, GetRegistry falls back to a fresh
        // Discover() — defensive only.
        var registry = Endpoints.BowireEndpointHelpers.GetRegistry();
        var url = ctx.Services?.GetKeyedService<string>(ServerUrlServiceKey)
            ?? "http://localhost";
        return (registry, url);
    }

    private static JsonObject BuildInputSchema(Models.BowireMethodInfo method)
    {
        // MCP Tool.InputSchema is a JsonNode (a JSON Schema document).
        // Build it the same shape the previous hand-rolled adapter
        // produced so existing MCP clients see a stable surface.
        var properties = new JsonObject();
        var required = new JsonArray();

        if (method.InputType?.Fields is not null)
        {
            foreach (var field in method.InputType.Fields)
            {
                properties[field.Name] = new JsonObject
                {
                    ["type"] = MapToJsonSchemaType(field.Type),
                    ["description"] = $"{field.Type} field #{field.Number}",
                };
                if (!string.Equals(field.Label, "optional", StringComparison.Ordinal)
                    && !field.IsRepeated && !field.IsMap)
                {
                    required.Add(field.Name);
                }
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }
        return schema;
    }

    private static string MapToJsonSchemaType(string protoType) => protoType switch
    {
        "string" => "string",
        "bool" => "boolean",
        "int32" or "int64" or "uint32" or "uint64" or "sint32" or "sint64"
            or "fixed32" or "fixed64" or "sfixed32" or "sfixed64" => "integer",
        "double" or "float" => "number",
        "bytes" => "string",
        "enum" => "string",
        "message" => "object",
        _ => "string",
    };
}
