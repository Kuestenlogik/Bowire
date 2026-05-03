// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.GraphQL;

/// <summary>
/// Bowire protocol plugin for GraphQL. Discovers a remote schema via
/// introspection (<see cref="GraphQLIntrospectionQuery"/>), surfaces the
/// query / mutation / subscription root operations as services, and
/// invokes them by building a parameterised operation string with
/// <see cref="GraphQLQueryBuilder"/>.
/// </summary>
public sealed class BowireGraphQLProtocol : IBowireProtocol
{
    private static readonly HttpClient s_http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    private static readonly string[] s_graphqlTransportWsSubProtocols = ["graphql-transport-ws"];

    public string Name => "GraphQL";
    public string Id => "graphql";

    // Official GraphQL logo (simpleicons) in brand pink.
    public string IconSvg => """<svg viewBox="0 0 24 24" fill="#e535ab" width="16" height="16" aria-hidden="true"><path d="M12.002 0a2.138 2.138 0 1 0 0 4.277 2.138 2.138 0 1 0 0-4.277zm8.54 4.931a2.138 2.138 0 1 0 0 4.277 2.138 2.138 0 1 0 0-4.277zm0 9.862a2.138 2.138 0 1 0 0 4.277 2.138 2.138 0 1 0 0-4.277zm-8.54 4.931a2.138 2.138 0 1 0 0 4.276 2.138 2.138 0 1 0 0-4.276zm-8.542-4.93a2.138 2.138 0 1 0 0 4.276 2.138 2.138 0 1 0 0-4.277zm0-9.863a2.138 2.138 0 1 0 0 4.277 2.138 2.138 0 1 0 0-4.277zm8.542-3.378L2.953 6.777v10.448l9.049 5.224 9.047-5.224V6.777zm0 1.601 7.66 13.27H4.34zm-1.387.371L3.97 15.037V7.363zm2.774 0 6.646 3.838v7.674zM5.355 17.44h13.293l-6.646 3.836z"/></svg>""";

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            return [];

        var endpoint = serverUrl.TrimEnd('/');

        try
        {
            var schemaResult = await SendOperationAsync(endpoint, GraphQLIntrospectionQuery.Query, null, null, ct);

            // Successful HTTP response — but the body might still be a GraphQL
            // 'errors' envelope (server doesn't allow introspection, or the URL
            // isn't a GraphQL endpoint at all). Treat that as "nothing to
            // discover" rather than letting the mapper crash on missing fields.
            if (!schemaResult.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return [];

            var mapper = new GraphQLSchemaMapper();
            return mapper.Map(data, serverUrl);
        }
        catch (HttpRequestException)
        {
            // Server isn't reachable or returned non-2xx — silently skip so
            // other protocol plugins on the same URL still get a chance.
            return [];
        }
        catch (JsonException)
        {
            // Body wasn't JSON at all — not a GraphQL endpoint.
            return [];
        }
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var operationKind = service switch
        {
            "Mutation" => "mutation",
            "Subscription" => "subscription",
            _ => "query"
        };

        var startedAt = DateTime.UtcNow;
        try
        {
            // Two payload shapes are accepted on the wire:
            //   1. variables-only — plain object like { "id": "abc" }. We
            //      synthesize the operation string for the user.
            //   2. full request   — { "query": "...", "variables": {...} }.
            //      We send it verbatim. This is what the GraphQL UI editor
            //      pane produces when the user edits the query manually.
            var (operation, variables) = TryParseFullRequest(jsonMessages, out var fullQuery)
                ? (fullQuery, ExtractVariables(jsonMessages))
                : BuildOperation(operationKind, service, method, jsonMessages);

            var endpoint = serverUrl.TrimEnd('/');
            var response = await SendOperationAsync(endpoint, operation, variables, metadata, ct);

            var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            var json = JsonSerializer.Serialize(response, s_indented);
            return new InvokeResult(json, elapsedMs, "OK", new Dictionary<string, string>());
        }
        catch (Exception ex)
        {
            var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            return new InvokeResult(null, elapsedMs, ex.Message, new Dictionary<string, string>());
        }
    }

    /// <summary>
    /// Optional metadata key. Set to <c>ws</c> to force the
    /// graphql-transport-ws transport, or <c>sse</c> to force graphql-sse.
    /// When unset, the plugin tries WebSocket first (if the WebSocket plugin
    /// is loaded) and falls back to SSE.
    /// </summary>
    public const string SubscriptionTransportMetadataKey = "X-Bowire-GraphQL-Subscription-Transport";

    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Subscriptions are only meaningful on the Subscription root type.
        // Anything else routes through InvokeAsync, but we still try for
        // forwards compatibility with servers that send streamed query results.
        var (operation, variables) = TryParseFullRequest(jsonMessages, out var fullQuery)
            ? (fullQuery, ExtractVariables(jsonMessages))
            : BuildOperation("subscription", service, method, jsonMessages);

        var (transportPreference, headers) = ExtractTransportPreference(metadata);

        // Default ordering: WebSocket first (graphql-transport-ws is the
        // canonical modern transport), then SSE. Forced preferences skip
        // straight to one transport.
        var registry = BowireProtocolRegistry.Discover();
        var wsChannel = registry.FindWebSocketChannel();

        if (transportPreference == "sse" || (transportPreference is null && wsChannel is null))
        {
            await foreach (var evt in StreamViaSseAsync(serverUrl, operation, variables, headers, ct))
                yield return evt;
            yield break;
        }

        if (wsChannel is null)
        {
            yield return JsonSerializer.Serialize(new
            {
                error = "graphql-transport-ws subscriptions require the WebSocket plugin "
                    + "(Kuestenlogik.Bowire.Protocol.WebSocket). Install it, or set the metadata header '"
                    + SubscriptionTransportMetadataKey + "' to 'sse' to use graphql-sse instead."
            }, s_indented);
            yield break;
        }

        await foreach (var evt in StreamViaGraphQLTransportWsAsync(wsChannel, serverUrl, operation, variables, headers, ct))
            yield return evt;
    }

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => Task.FromResult<IBowireChannel?>(null);

    private static (string? Preference, Dictionary<string, string>? Headers) ExtractTransportPreference(
        Dictionary<string, string>? metadata)
    {
        if (metadata is null) return (null, null);

        string? matched = null;
        string? value = null;
        foreach (var (k, v) in metadata)
        {
            if (string.Equals(k, SubscriptionTransportMetadataKey, StringComparison.OrdinalIgnoreCase))
            {
                matched = k;
                value = NormalizePreference(v);
                break;
            }
        }

        if (matched is null) return (null, metadata);

        var filtered = new Dictionary<string, string>(metadata.Count - 1, StringComparer.Ordinal);
        foreach (var (k, v) in metadata)
        {
            if (!string.Equals(k, matched, StringComparison.Ordinal)) filtered[k] = v;
        }
        return (value, filtered);
    }

    private static string? NormalizePreference(string? raw)
    {
        if (raw is null) return null;
        return raw.Trim() switch
        {
            "ws" or "WS" or "Ws" => "ws",
            "sse" or "SSE" or "Sse" => "sse",
            _ => raw.Trim()
        };
    }

    /// <summary>
    /// graphql-sse transport (https://github.com/enisdenjo/graphql-sse).
    /// Single-connection mode: POST the operation to the GraphQL endpoint
    /// with <c>Accept: text/event-stream</c>, then read the resulting SSE
    /// stream and yield each <c>event: next</c> payload as a JSON envelope.
    /// </summary>
    private static async IAsyncEnumerable<string> StreamViaSseAsync(
        string serverUrl,
        string operation,
        JsonElement? variables,
        Dictionary<string, string>? headers,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var endpoint = serverUrl.TrimEnd('/');
        var payload = variables.HasValue && variables.Value.ValueKind == JsonValueKind.Object
            ? (object)new { query = operation, variables = variables.Value }
            : new { query = operation };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload, options: s_jsonOptions)
        };
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (headers is not null)
        {
            foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
        }

        HttpResponseMessage? response = null;
        string? sendError = null;
        try
        {
            response = await s_http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            response?.Dispose();
            response = null;
            sendError = "graphql-sse: " + ex.Message;
        }

        if (response is null)
        {
            yield return JsonSerializer.Serialize(new { error = sendError ?? "graphql-sse send failed" }, s_indented);
            yield break;
        }

        // Compiler flow analysis takes the early-return above as proof that
        // `response` is non-null from here on, so the `using` and the finally
        // block work without any `!` suppressions.
        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            string? eventType = null;
            var dataLines = new List<string>();

            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                if (line is null) break;

                if (line.Length == 0)
                {
                    if (dataLines.Count > 0)
                    {
                        var data = string.Join("\n", dataLines);
                        if (eventType is null or "next")
                            yield return data;
                        if (eventType == "complete") yield break;
                    }
                    eventType = null;
                    dataLines.Clear();
                    continue;
                }

                if (line.StartsWith(':')) continue; // SSE comment / keepalive
                if (line.StartsWith("data:", StringComparison.Ordinal))
                    dataLines.Add(line.Length > 5 ? line[5..].TrimStart() : "");
                else if (line.StartsWith("event:", StringComparison.Ordinal))
                    eventType = line[6..].TrimStart();
            }
        }
    }

    /// <summary>
    /// graphql-transport-ws (https://github.com/enisdenjo/graphql-ws/blob/master/PROTOCOL.md).
    /// Opens a WebSocket with the <c>graphql-transport-ws</c> sub-protocol,
    /// sends <c>connection_init</c>, awaits <c>connection_ack</c>, then
    /// sends a <c>subscribe</c> message and yields every <c>next</c> payload
    /// until the server sends <c>complete</c> or the cancellation token fires.
    /// </summary>
    private static async IAsyncEnumerable<string> StreamViaGraphQLTransportWsAsync(
        IInlineWebSocketChannel wsFactory,
        string serverUrl,
        string operation,
        JsonElement? variables,
        Dictionary<string, string>? headers,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var wsUrl = HttpToWs(serverUrl);
        IBowireChannel? channel = null;
        string? connectError = null;
        try
        {
            channel = await wsFactory.OpenAsync(wsUrl, s_graphqlTransportWsSubProtocols, headers, ct);
        }
        catch (Exception ex)
        {
            connectError = "graphql-ws connect failed: " + ex.Message;
        }

        if (channel is null)
        {
            yield return JsonSerializer.Serialize(new { error = connectError ?? "graphql-ws connect failed" }, s_indented);
            yield break;
        }

        // From here on the compiler's flow analysis knows `channel` is non-null,
        // so the `await using` and every member access compiles without `!`.
        await using (channel)
        {
            // 1. Send connection_init and wait for connection_ack on the
            //    response stream. We pre-start the responses enumerator so
            //    we don't lose the ack message.
            await channel.SendAsync("{\"type\":\"connection_init\"}", ct);

            var subscribed = false;
            const string operationId = "1";

            await foreach (var raw in channel.ReadResponsesAsync(ct))
            {
                JsonElement msg;
                try { msg = JsonSerializer.Deserialize<JsonElement>(raw); }
                catch { continue; }

                // Channel envelopes from the WebSocket plugin look like
                // { type: "text"|"binary"|"close", text?, base64?, ... }.
                // Unwrap text frames into their JSON-RPC payload.
                if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("type", out var envType) &&
                    envType.GetString() == "text" && msg.TryGetProperty("text", out var inner))
                {
                    var innerText = inner.GetString();
                    if (string.IsNullOrEmpty(innerText)) continue;
                    try { msg = JsonSerializer.Deserialize<JsonElement>(innerText); }
                    catch { continue; }
                }

                if (msg.ValueKind != JsonValueKind.Object) continue;
                if (!msg.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString();

                switch (type)
                {
                    case "connection_ack":
                        if (subscribed) break;
                        subscribed = true;
                        var subPayload = variables.HasValue && variables.Value.ValueKind == JsonValueKind.Object
                            ? (object)new { query = operation, variables = variables.Value }
                            : new { query = operation };
                        var subscribe = new
                        {
                            id = operationId,
                            type = "subscribe",
                            payload = subPayload
                        };
                        await channel.SendAsync(JsonSerializer.Serialize(subscribe, s_jsonOptions), ct);
                        break;

                    case "next":
                        if (msg.TryGetProperty("payload", out var payload))
                            yield return JsonSerializer.Serialize(payload, s_indented);
                        break;

                    case "error":
                        if (msg.TryGetProperty("payload", out var errPayload))
                            yield return JsonSerializer.Serialize(new { errors = errPayload }, s_indented);
                        else
                            yield return JsonSerializer.Serialize(new { error = "graphql-ws error" }, s_indented);
                        yield break;

                    case "complete":
                        yield break;

                    case "ping":
                        await channel.SendAsync("{\"type\":\"pong\"}", ct);
                        break;

                    case "pong":
                        // ignore — server-initiated heartbeat reply
                        break;
                }
            }
        }
    }

    private static string HttpToWs(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "ws://" + url["http://".Length..];
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "wss://" + url["https://".Length..];
        return url;
    }

    private static bool TryParseFullRequest(List<string> jsonMessages, out string query)
    {
        query = "";
        if (jsonMessages.Count == 0 || string.IsNullOrWhiteSpace(jsonMessages[0]))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(jsonMessages[0]);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("query", out var q)) return false;
            if (q.ValueKind != JsonValueKind.String) return false;

            var s = q.GetString();
            if (string.IsNullOrWhiteSpace(s)) return false;

            query = s;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static JsonElement ExtractVariables(List<string> jsonMessages)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonMessages[0]);
            if (doc.RootElement.TryGetProperty("variables", out var v) && v.ValueKind == JsonValueKind.Object)
                return v.Clone();
        }
        catch
        {
            // Fall through
        }
        return default;
    }

    private static (string Operation, JsonElement Variables) BuildOperation(
        string operationKind, string service, string method, List<string> jsonMessages)
    {
        // We need the BowireMethodInfo to know argument types — but the
        // /api/invoke path doesn't pass the method object back to us. Re-derive
        // a minimal one from the JSON body so we can still build a valid
        // operation. The variables come from the user's form submission.
        var variablesJson = jsonMessages.Count > 0 ? jsonMessages[0] : "{}";
        var stub = new BowireMethodInfo(
            Name: method,
            FullName: service + "/" + method,
            ClientStreaming: false,
            ServerStreaming: false,
            InputType: BuildStubInput(variablesJson),
            OutputType: new BowireMessageInfo("GraphQLResponse", "graphql.GraphQLResponse", []),
            MethodType: "Unary");

        return GraphQLQueryBuilder.Build(operationKind, stub, variablesJson);
    }

    /// <summary>
    /// When invocation comes through <c>/api/invoke</c> we no longer have the
    /// strongly-typed argument list from discovery. Synthesize one from the
    /// runtime variables payload so the query builder can name them and emit
    /// a parameter list. Loses GraphQL-typing precision (everything becomes
    /// String), but the server still resolves the operation as long as it
    /// supports implicit type coercion.
    /// </summary>
    private static BowireMessageInfo BuildStubInput(string variablesJson)
    {
        var fields = new List<BowireFieldInfo>();
        try
        {
            using var doc = JsonDocument.Parse(variablesJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var i = 1;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var (type, repeated) = InferGraphQLType(prop.Value);
                    fields.Add(new BowireFieldInfo(
                        Name: prop.Name,
                        Number: i++,
                        Type: type,
                        Label: "optional",
                        IsMap: false,
                        IsRepeated: repeated,
                        MessageType: null,
                        EnumValues: null));
                }
            }
        }
        catch
        {
            // Malformed input — operation will run with zero arguments
        }

        return new BowireMessageInfo("Variables", "Variables", fields);
    }

    private static (string Type, bool Repeated) InferGraphQLType(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => ("bool", false),
        JsonValueKind.Number => value.TryGetInt64(out _) ? ("int64", false) : ("double", false),
        JsonValueKind.Array => ("string", true),
        JsonValueKind.Object => ("message", false),
        _ => ("string", false)
    };

    private static async Task<JsonElement> SendOperationAsync(
        string endpoint,
        string query,
        JsonElement? variables,
        Dictionary<string, string>? headers,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        if (headers is not null)
        {
            foreach (var (key, value) in headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        var payload = variables.HasValue && variables.Value.ValueKind == JsonValueKind.Object
            ? (object)new { query, variables = variables.Value }
            : new { query };

        request.Content = JsonContent.Create(payload, options: s_jsonOptions);

        using var response = await s_http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(s_jsonOptions, ct);
    }
}
