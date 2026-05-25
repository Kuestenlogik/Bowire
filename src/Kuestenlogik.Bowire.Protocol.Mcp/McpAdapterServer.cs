// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.Mcp;

/// <summary>
/// MCP adapter — exposes Bowire's discovered API services as MCP tools so
/// that AI agents (Claude, Copilot, Cursor) can call them. This is a
/// development-time convenience feature, NOT a real protocol that gets
/// discovered. Must be opted into explicitly via
/// <see cref="McpAdapterEndpoints.MapBowireMcpAdapter"/>.
/// </summary>
internal sealed class McpAdapterServer
{
    private readonly BowireProtocolRegistry _registry;
    private readonly string _serverUrl;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public McpAdapterServer(BowireProtocolRegistry registry, string serverUrl)
    {
        _registry = registry;
        _serverUrl = serverUrl;
    }

    public async Task<JsonElement> HandleMessageAsync(JsonElement message, CancellationToken ct)
    {
        var method = message.GetProperty("method").GetString();
        var id = message.TryGetProperty("id", out var idProp) ? idProp : default;

        JsonElement? result = method switch
        {
            "initialize" => HandleInitialize(),
            "notifications/initialized" => null,
            "tools/list" => await HandleToolsListAsync(ct),
            "tools/call" => await HandleToolsCallAsync(message.GetProperty("params"), ct),
            "resources/list" => await HandleResourcesListAsync(ct),
            "resources/read" => await HandleResourcesReadAsync(message.GetProperty("params"), ct),
            "prompts/list" => HandlePromptsList(),
            "prompts/get" => HandlePromptsGet(message.GetProperty("params")),
            "ping" => HandlePing(),
            _ => CreateError(id, -32601, $"Method not found: {method}")
        };

        if (result is null) return default; // Notification -- no response

        return CreateResponse(id, result.Value);
    }

    private JsonElement HandleInitialize()
    {
        return JsonSerializer.SerializeToElement(new
        {
            protocolVersion = "2024-11-05",
            capabilities = new
            {
                tools = new { listChanged = false },
                resources = new { listChanged = false, subscribe = false },
                prompts = new { listChanged = false },
            },
            serverInfo = new
            {
                name = "bowire",
                version = "0.9.4"
            }
        }, _jsonOptions);
    }

    private static JsonElement HandlePing()
    {
        return JsonSerializer.SerializeToElement(new { });
    }

    private async Task<JsonElement> HandleToolsListAsync(CancellationToken ct)
    {
        var tools = new List<object>();

        foreach (var protocol in _registry.Protocols)
        {
            // Skip the MCP protocol itself -- it doesn't discover services
            if (protocol.Id == "mcp") continue;

            List<BowireServiceInfo> services;
            try
            {
                services = await protocol.DiscoverAsync(_serverUrl, showInternalServices: false, ct);
            }
            catch
            {
                continue;
            }

            foreach (var service in services)
            {
                foreach (var method in service.Methods)
                {
                    // Only expose unary methods as tools (streaming doesn't fit MCP well)
                    if (method.MethodType != "Unary") continue;

                    var toolName = $"{service.Name.Replace('.', '_')}_{method.Name}";
                    var description = $"{method.MethodType} {protocol.Name} method: {service.Name}/{method.Name}";

                    var properties = new Dictionary<string, object>();
                    var required = new List<string>();

                    if (method.InputType?.Fields is not null)
                    {
                        foreach (var field in method.InputType.Fields)
                        {
                            properties[field.Name] = new
                            {
                                type = MapToJsonSchemaType(field.Type),
                                description = $"{field.Type} field #{field.Number}"
                            };
                            if (field.Label != "optional" && !field.IsRepeated && !field.IsMap)
                                required.Add(field.Name);
                        }
                    }

                    tools.Add(new
                    {
                        name = toolName,
                        description,
                        inputSchema = new
                        {
                            type = "object",
                            properties,
                            required = required.Count > 0 ? required : null
                        }
                    });
                }
            }
        }

        return JsonSerializer.SerializeToElement(new { tools }, _jsonOptions);
    }

    private async Task<JsonElement> HandleToolsCallAsync(JsonElement parameters, CancellationToken ct)
    {
        var toolName = parameters.GetProperty("name").GetString()
            ?? throw new InvalidOperationException("MCP tools/call request is missing the 'name' field.");
        var arguments = parameters.TryGetProperty("arguments", out var argsProp)
            ? argsProp
            : JsonSerializer.SerializeToElement(new { });

        // Parse tool name back to service/method
        // Format: "package_ServiceName_MethodName"
        // We need to find the matching protocol + service + method

        foreach (var protocol in _registry.Protocols)
        {
            if (protocol.Id == "mcp") continue;

            List<BowireServiceInfo> services;
            try { services = await protocol.DiscoverAsync(_serverUrl, showInternalServices: false, ct); }
            catch { continue; }

            foreach (var service in services)
            {
                foreach (var method in service.Methods)
                {
                    var candidateName = $"{service.Name.Replace('.', '_')}_{method.Name}";
                    if (candidateName != toolName) continue;

                    // Found the matching method -- invoke it
                    try
                    {
                        var jsonBody = arguments.GetRawText();
                        var result = await protocol.InvokeAsync(
                            _serverUrl, service.Name, method.Name,
                            [jsonBody], showInternalServices: false,
                            null, ct);

                        return JsonSerializer.SerializeToElement(new
                        {
                            content = new[]
                            {
                                new
                                {
                                    type = "text",
                                    text = result.Response ?? "null"
                                }
                            }
                        }, _jsonOptions);
                    }
                    catch (Exception ex)
                    {
                        return JsonSerializer.SerializeToElement(new
                        {
                            content = new[]
                            {
                                new { type = "text", text = $"Error: {ex.Message}" }
                            },
                            isError = true
                        }, _jsonOptions);
                    }
                }
            }
        }

        return CreateError(default, -32602, $"Tool not found: {toolName}");
    }

    // ----------------------------------------------------------------
    // resources/* — per-service schema dumps. Each discovered service
    // becomes one resource at `bowire-service://{plugin}/{service}` so
    // an agent can read the full method+field tree (proto / OpenAPI /
    // hub schema, depending on the plugin) before deciding which
    // method to call. Cheaper than spamming tools/list, and a natural
    // pendant to the tools/call -> service.method flow.
    // ----------------------------------------------------------------

    private async Task<JsonElement> HandleResourcesListAsync(CancellationToken ct)
    {
        var resources = new List<object>();
        foreach (var protocol in _registry.Protocols)
        {
            if (protocol.Id == "mcp") continue;
            List<BowireServiceInfo> services;
            try { services = await protocol.DiscoverAsync(_serverUrl, showInternalServices: false, ct); }
            catch { continue; }

            foreach (var service in services)
            {
                var uri = $"bowire-service://{protocol.Id}/{service.Name}";
                resources.Add(new
                {
                    uri,
                    name = service.Name,
                    description = $"{protocol.Name} service schema — {service.Methods.Count} method(s).",
                    mimeType = "application/json",
                });
            }
        }
        return JsonSerializer.SerializeToElement(new { resources }, _jsonOptions);
    }

    private async Task<JsonElement> HandleResourcesReadAsync(JsonElement parameters, CancellationToken ct)
    {
        var uri = parameters.GetProperty("uri").GetString() ?? string.Empty;
        // bowire-service://<plugin>/<service>
        const string scheme = "bowire-service://";
        if (!uri.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return CreateError(default, -32602, $"Unsupported resource URI: {uri}");

        var rest = uri.Substring(scheme.Length);
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
            return CreateError(default, -32602, $"Malformed resource URI (expected bowire-service://<plugin>/<service>): {uri}");

        var pluginId = rest.Substring(0, slash);
        var serviceName = rest.Substring(slash + 1);

        var protocol = _registry.Protocols.FirstOrDefault(
            p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (protocol is null)
            return CreateError(default, -32602, $"Unknown plugin id: {pluginId}");

        List<BowireServiceInfo> services;
        try { services = await protocol.DiscoverAsync(_serverUrl, showInternalServices: false, ct); }
        catch (Exception ex)
        {
            return CreateError(default, -32603, $"Discovery failed for {pluginId}: {ex.Message}");
        }

        var service = services.FirstOrDefault(
            s => string.Equals(s.Name, serviceName, StringComparison.Ordinal));
        if (service is null)
            return CreateError(default, -32602, $"Unknown service: {serviceName}");

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
        }, _jsonOptions);

        return JsonSerializer.SerializeToElement(new
        {
            contents = new[]
            {
                new
                {
                    uri,
                    mimeType = "application/json",
                    text = payload,
                }
            }
        }, _jsonOptions);
    }

    // ----------------------------------------------------------------
    // prompts/* — generic "describe-service" + "generate-sample-request"
    // templates that work against any discovered service. The agent
    // picks the prompt, fills in the service/method arguments, and
    // the rendered message tells it which tool to call to gather the
    // facts.
    // ----------------------------------------------------------------

    private JsonElement HandlePromptsList()
    {
        return JsonSerializer.SerializeToElement(new
        {
            prompts = new object[]
            {
                new
                {
                    name = "describe-service",
                    description = "Summarise what a discovered service does — method list, request/response shape, likely intent. Reads the service schema via the bowire-service:// resource.",
                    arguments = new[]
                    {
                        new { name = "service", description = "Fully-qualified service name (e.g. weather.WeatherService).", required = true },
                    }
                },
                new
                {
                    name = "generate-sample-request",
                    description = "Generate a valid JSON request body for a service method, ready to feed into a tools/call. Uses the field types from the service schema and picks plausible sample values.",
                    arguments = new[]
                    {
                        new { name = "service", description = "Fully-qualified service name.", required = true },
                        new { name = "method", description = "Method name on that service.", required = true },
                    }
                },
            }
        }, _jsonOptions);
    }

    private JsonElement HandlePromptsGet(JsonElement parameters)
    {
        var name = parameters.GetProperty("name").GetString() ?? string.Empty;
        var args = parameters.TryGetProperty("arguments", out var a) ? a : default;

        string Arg(string key)
            => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v)
                ? v.GetString() ?? "" : "";

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

        return JsonSerializer.SerializeToElement(new
        {
            description = name switch
            {
                "describe-service" => "Three-paragraph summary of a discovered service.",
                "generate-sample-request" => "JSON request body ready for tools/call.",
                _ => "Unknown prompt",
            },
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new { type = "text", text },
                }
            }
        }, _jsonOptions);
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
        _ => "string"
    };

    private JsonElement CreateResponse(JsonElement id, JsonElement result)
    {
        return JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = id.ValueKind != JsonValueKind.Undefined ? id : JsonSerializer.SerializeToElement(0),
            result
        }, _jsonOptions);
    }

    private static JsonElement CreateError(JsonElement id, int code, string message)
    {
        return JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = id.ValueKind != JsonValueKind.Undefined ? id : JsonSerializer.SerializeToElement(0),
            error = new { code, message }
        });
    }
}
