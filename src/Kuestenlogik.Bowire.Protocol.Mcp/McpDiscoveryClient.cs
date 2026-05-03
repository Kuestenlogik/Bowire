// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.Mcp;

/// <summary>
/// JSON-RPC client for the MCP <i>streamable HTTP</i> transport: a single
/// endpoint that accepts POST requests with JSON-RPC messages and replies
/// with JSON. Used by <see cref="BowireMcpProtocol"/> to discover and invoke
/// tools, resources, and prompts on a remote MCP server.
/// </summary>
/// <remarks>
/// The classic SSE+POST transport (separate <c>/sse</c> event stream + message
/// POST endpoint) is not yet supported here — when an MCP server only speaks
/// SSE, point Bowire at the message endpoint URL directly. The Bowire MCP
/// adapter accepts both transports, so it works either way.
/// </remarks>
internal sealed class McpDiscoveryClient
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly Dictionary<string, string>? _headers;
    private int _nextRequestId;

    public McpDiscoveryClient(HttpClient http, string endpoint, Dictionary<string, string>? headers = null)
    {
        _http = http;
        _endpoint = endpoint;
        _headers = headers;
    }

    /// <summary>
    /// Sends <c>initialize</c> + <c>notifications/initialized</c> to the server,
    /// returning the server's reported capabilities.
    /// </summary>
    public async Task<JsonElement> InitializeAsync(CancellationToken ct)
    {
        var initResult = await SendRequestAsync("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "bowire", version = "0.9.4" }
        }, ct);

        // Notify server we're ready
        await SendNotificationAsync("notifications/initialized", null, ct);

        return initResult;
    }

    public Task<JsonElement> ListToolsAsync(CancellationToken ct) =>
        SendRequestAsync("tools/list", new { }, ct);

    public Task<JsonElement> ListResourcesAsync(CancellationToken ct) =>
        SendRequestAsync("resources/list", new { }, ct);

    public Task<JsonElement> ListPromptsAsync(CancellationToken ct) =>
        SendRequestAsync("prompts/list", new { }, ct);

    public Task<JsonElement> CallToolAsync(string name, JsonElement arguments, CancellationToken ct) =>
        SendRequestAsync("tools/call", new
        {
            name,
            arguments = arguments.ValueKind == JsonValueKind.Undefined
                ? (object)new { }
                : arguments
        }, ct);

    public Task<JsonElement> ReadResourceAsync(string uri, CancellationToken ct) =>
        SendRequestAsync("resources/read", new { uri }, ct);

    public Task<JsonElement> GetPromptAsync(string name, JsonElement arguments, CancellationToken ct) =>
        SendRequestAsync("prompts/get", new
        {
            name,
            arguments = arguments.ValueKind == JsonValueKind.Undefined
                ? (object)new { }
                : arguments
        }, ct);

    private async Task<JsonElement> SendRequestAsync(string method, object @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var request = new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params
        };

        using var httpRequest = BuildPostRequest(request);
        using var response = await _http.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        // The streamable HTTP transport returns either application/json with the
        // single response or text/event-stream with framed events. Try JSON first.
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";

        if (contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            // SSE-framed response — read until we see a 'message' event
            var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            return await ReadSseResponseAsync(reader, id, ct);
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(s_jsonOptions, ct);
        return ExtractResult(json, id);
    }

    private async Task SendNotificationAsync(string method, object? @params, CancellationToken ct)
    {
        var notification = @params is null
            ? (object)new { jsonrpc = "2.0", method }
            : new { jsonrpc = "2.0", method, @params };

        using var httpRequest = BuildPostRequest(notification);
        using var response = await _http.SendAsync(httpRequest, ct);
        // Notifications expect 200 or 204 with no body
        if (!response.IsSuccessStatusCode)
            response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage BuildPostRequest(object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(payload, options: s_jsonOptions)
        };

        // Forward auth / metadata headers from the active environment so MCP
        // servers behind Bearer / API-Key / OAuth see them. The auth-helper
        // pipeline already populated `_headers` before InvokeAsync was called;
        // we just need to attach them on every JSON-RPC request the client
        // sends (initialize, tools/list, tools/call, ...).
        if (_headers is not null)
        {
            foreach (var (key, value) in _headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        return request;
    }

    private static async Task<JsonElement> ReadSseResponseAsync(StreamReader reader, int expectedId, CancellationToken ct)
    {
        var dataLines = new List<string>();
        string? eventType = null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            if (line.Length == 0)
            {
                if (dataLines.Count > 0 && (eventType is null or "message"))
                {
                    var data = string.Join("\n", dataLines);
                    var json = JsonSerializer.Deserialize<JsonElement>(data);
                    if (json.TryGetProperty("id", out var idProp) &&
                        idProp.TryGetInt32(out var msgId) && msgId == expectedId)
                    {
                        return ExtractResult(json, expectedId);
                    }
                }
                dataLines.Clear();
                eventType = null;
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
                dataLines.Add(line.Length > 5 ? line[5..].TrimStart() : "");
            else if (line.StartsWith("event:", StringComparison.Ordinal))
                eventType = line[6..].TrimStart();
        }

        throw new InvalidOperationException($"MCP server closed the SSE stream before responding to request {expectedId}.");
    }

    private static JsonElement ExtractResult(JsonElement envelope, int expectedId)
    {
        if (envelope.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : "Unknown MCP error";
            throw new InvalidOperationException($"MCP error {code}: {message}");
        }

        if (envelope.TryGetProperty("result", out var result))
            return result;

        throw new InvalidOperationException($"MCP response for request {expectedId} has neither 'result' nor 'error'.");
    }

    /// <summary>
    /// Maps an MCP tool input schema (JSON Schema, type=object) to a
    /// <see cref="BowireMessageInfo"/> so the standard form-based UI can
    /// render it.
    /// </summary>
    public static BowireMessageInfo MapInputSchema(string toolName, JsonElement schema)
    {
        var fields = new List<BowireFieldInfo>();
        if (schema.ValueKind != JsonValueKind.Object)
            return new BowireMessageInfo(toolName + "Input", toolName + "Input", fields);

        HashSet<string>? required = null;
        if (schema.TryGetProperty("required", out var requiredProp) && requiredProp.ValueKind == JsonValueKind.Array)
        {
            required = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in requiredProp.EnumerateArray())
                if (r.GetString() is { } s) required.Add(s);
        }

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            var i = 1;
            foreach (var prop in properties.EnumerateObject())
            {
                var (type, isRepeated, nested) = ResolvePropertyType(prop.Name, prop.Value);
                var description = prop.Value.TryGetProperty("description", out var descProp)
                    ? descProp.GetString()
                    : null;
                var label = required is not null && required.Contains(prop.Name) ? "required" : "optional";

                fields.Add(new BowireFieldInfo(
                    Name: prop.Name,
                    Number: i++,
                    Type: type,
                    Label: label,
                    IsMap: false,
                    IsRepeated: isRepeated,
                    MessageType: nested,
                    EnumValues: null)
                {
                    Required = required is not null && required.Contains(prop.Name),
                    Description = description,
                    Source = "body"
                });
            }
        }

        return new BowireMessageInfo(toolName + "Input", toolName + "Input", fields);
    }

    private static (string Type, bool IsRepeated, BowireMessageInfo? Nested) ResolvePropertyType(
        string propertyName, JsonElement propertySchema)
    {
        if (propertySchema.ValueKind != JsonValueKind.Object)
            return ("string", false, null);

        var jsonType = propertySchema.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString()
            : null;

        switch (jsonType)
        {
            case "array":
                if (propertySchema.TryGetProperty("items", out var items))
                {
                    var (innerType, _, innerNested) = ResolvePropertyType(propertyName + "Item", items);
                    return (innerType, true, innerNested);
                }
                return ("string", true, null);

            case "object":
                return ("message", false, MapInputSchema(propertyName, propertySchema));

            case "integer":
                return ("int64", false, null);

            case "number":
                return ("double", false, null);

            case "boolean":
                return ("bool", false, null);

            case "string":
            default:
                return ("string", false, null);
        }
    }
}
