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
                tools = new { listChanged = false }
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
