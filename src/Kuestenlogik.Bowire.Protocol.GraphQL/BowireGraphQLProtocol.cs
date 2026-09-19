// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.GraphQL;

/// <summary>
/// Bowire protocol plugin for GraphQL. Discovers a remote schema via
/// introspection (<see cref="GraphQLIntrospectionQuery"/>), surfaces the
/// query / mutation / subscription root operations as services, and
/// invokes them by building a parameterised operation string with
/// <see cref="GraphQLQueryBuilder"/>.
/// </summary>
public sealed class BowireGraphQLProtocol : IBowireProtocol, IDisposable
{
    // Built lazily from BowireHttpClientFactory in Initialize() so the
    // localhost-cert opt-in (Bowire:TrustLocalhostCert) reaches the
    // certificate validation callback. Falls back to a vanilla HttpClient
    // for test paths that skip Initialize.
    private HttpClient _http = new()
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

    /// <summary>
    /// What the last discovery found, per endpoint (#710).
    /// </summary>
    /// <remarks>
    /// <para>
    /// /api/invoke carries a service and a method name, not the discovered
    /// <see cref="BowireMethodInfo"/>. Without this the plugin had to
    /// re-derive a stub from the request body, and a stub has no output
    /// type — which is why every generated operation selected nothing but
    /// <c>__typename</c>.
    /// </para>
    /// <para>
    /// Bounded by the number of endpoints an operator discovers in one
    /// session, and each entry is a schema they asked for. A miss is not an
    /// error: the stub path still runs and still produces a valid
    /// operation, just a less useful one.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, List<BowireServiceInfo>> _discovered =
        new(StringComparer.OrdinalIgnoreCase);

    // Test-only override for the protocol registry used by the
    // subscription dispatch in InvokeStreamAsync. Production callers leave
    // it null and the regular global discovery scan runs; tests can hand in
    // a curated registry (e.g. one without the WebSocket plugin) to drive
    // the "WS plugin not loaded" error envelope and the
    // graphql-transport-ws frame-shape fallbacks without orchestrating an
    // assembly-discovery wobble.
    internal static Func<BowireProtocolRegistry>? RegistryFactory { get; set; }

    public string Name => "GraphQL";
    public string Description => "Query / Mutation / Subscription over HTTP and WebSocket.";

    // #691 - the catalogue key beside the text; the text stays
    // as the fallback for a host whose catalogue lacks the entry.
    public string DescriptionKey => "plugin.graphql.description";
    public string Id => "graphql";

    // Official GraphQL logo (simpleicons) in brand pink.
    public string IconSvg => """<svg viewBox="0 0 24 24" fill="#e535ab" width="16" height="16" aria-hidden="true"><path d="M12.002 0a2.138 2.138 0 1 0 0 4.277 2.138 2.138 0 1 0 0-4.277zm8.54 4.931a2.138 2.138 0 1 0 0 4.277 2.138 2.138 0 1 0 0-4.277zm0 9.862a2.138 2.138 0 1 0 0 4.277 2.138 2.138 0 1 0 0-4.277zm-8.54 4.931a2.138 2.138 0 1 0 0 4.276 2.138 2.138 0 1 0 0-4.276zm-8.542-4.93a2.138 2.138 0 1 0 0 4.276 2.138 2.138 0 1 0 0-4.277zm0-9.863a2.138 2.138 0 1 0 0 4.277 2.138 2.138 0 1 0 0-4.277zm8.542-3.378L2.953 6.777v10.448l9.049 5.224 9.047-5.224V6.777zm0 1.601 7.66 13.27H4.34zm-1.387.371L3.97 15.037V7.363zm2.774 0 6.646 3.838v7.674zM5.355 17.44h13.293l-6.646 3.836z"/></svg>""";

    public void Initialize(IServiceProvider? serviceProvider)
    {
        var config = serviceProvider?.GetService<IConfiguration>();
        _http = BowireHttpClientFactory.Create(config, Id, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Dispose the lazily-built <see cref="HttpClient"/>. Same lifecycle
    /// contract as the REST plugin — the registry that owns the plugin
    /// instance disposes it at host shutdown.
    /// </summary>
    public void Dispose()
    {
        _http.Dispose();
    }

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
            var services = mapper.Map(data, serverUrl);
            // #710 - remembered so InvokeAsync can build a real selection
            // set instead of falling back to __typename.
            if (services.Count > 0) _discovered[endpoint] = services;
            return services;
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
            var endpoint = serverUrl.TrimEnd('/');
            var verbatim = TryParseFullRequest(jsonMessages, out var fullQuery);
            var (operation, variables) = verbatim
                ? (fullQuery, ExtractVariables(jsonMessages))
                : await BuildOperationAsync(
                    operationKind, service, method, jsonMessages, endpoint, mayIntrospect: true, ct);

            // #710 - a document that does not parse is answered here rather
            // than sent. Only the verbatim path: an operation Bowire built
            // itself parses by construction. The message names Bowire so an
            // operator can tell a local parser complaint from their
            // server's.
            if (verbatim && GraphQLDocumentInfo.SyntaxError(operation) is { } syntax)
            {
                return new InvokeResult(
                    null,
                    (long)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                    "Bowire could not parse this GraphQL document: " + syntax,
                    new Dictionary<string, string>());
            }

            // #710 - only the verbatim path needs resolving. An operation we
            // built ourselves has exactly one definition and we named it
            // `method`, so there is nothing to disambiguate.
            var operationName = verbatim
                ? GraphQLDocumentInfo.ResolveOperationName(operation, ExtractOperationName(jsonMessages))
                : method;

            // #713 - GET is opt-in, and only for queries. The
            // GraphQL-over-HTTP spec allows GET for queries alone, and the
            // reason is not pedantry: a mutation sent over GET is a request
            // that intermediaries treat as safe to retry, prefetch and
            // cache. Refusing here is better than letting a proxy decide to
            // run somebody's mutation twice.
            var (useGet, sendHeaders) = ExtractHttpMethod(metadata);
            if (useGet && OperationKindOf(operation, operationKind) != "query")
            {
                return new InvokeResult(
                    null,
                    (long)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                    $"{HttpMethodMetadataKey}=get applies to queries only — this is a "
                    + OperationKindOf(operation, operationKind)
                    + ". GraphQL over GET is defined for queries because intermediaries may "
                    + "retry, prefetch or cache a GET.",
                    new Dictionary<string, string>());
            }

            var response = await SendOperationAsync(
                endpoint, operation, variables, sendHeaders, ct, operationName, useGet);

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

    /// <summary>
    /// Optional metadata key. Set to <c>get</c> to send queries over
    /// <c>GET</c> instead of <c>POST</c> (#713).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bowire has always posted. That works against nearly every server and
    /// is the right default, but it leaves two cases unreachable: a CDN or
    /// cache in front of the API, which can only cache a GET, and a server
    /// configured to accept queries over GET alone.
    /// </para>
    /// <para>
    /// Opt-in rather than automatic, because the choice has consequences a
    /// caller should make deliberately: a GET puts the whole document in
    /// the URL, where proxies and access logs keep it, and long documents
    /// meet a URL length limit nobody controls.
    /// </para>
    /// </remarks>
    public const string HttpMethodMetadataKey = "X-Bowire-GraphQL-Http-Method";

    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Subscriptions are only meaningful on the Subscription root type.
        // Anything else routes through InvokeAsync, but we still try for
        // forwards compatibility with servers that send streamed query results.
        var verbatim = TryParseFullRequest(jsonMessages, out var fullQuery);
        var (operation, variables) = verbatim
            ? (fullQuery, ExtractVariables(jsonMessages))
            // mayIntrospect: false — see SchemaForAsync. Probing a
            // subscription endpoint opens a stream instead of answering.
            : await BuildOperationAsync(
                "subscription", service, method, jsonMessages, serverUrl.TrimEnd('/'),
                mayIntrospect: false, ct);

        // #710 - a subscription needs the name as much as a query does, and
        // for the same reason: a document with several operations is not
        // resolvable without it. Both transports carry it.
        var operationName = verbatim
            ? GraphQLDocumentInfo.ResolveOperationName(operation, ExtractOperationName(jsonMessages))
            : method;

        var (transportPreference, headers) = ExtractTransportPreference(metadata);

        // Default ordering: WebSocket first (graphql-transport-ws is the
        // canonical modern transport), then SSE. Forced preferences skip
        // straight to one transport.
        var registry = RegistryFactory?.Invoke() ?? BowireProtocolRegistry.Discover();
        var wsChannel = registry.FindWebSocketChannel();

        if (transportPreference == "sse" || (transportPreference is null && wsChannel is null))
        {
            await foreach (var evt in StreamViaSseAsync(serverUrl, operation, variables, headers, ct, operationName))
                yield return evt;
            yield break;
        }

        if (wsChannel is null)
        {
            // #712 - this is a missing prerequisite on this host, not a
            // failure of the server being called. The kind says so.
            yield return BowireStreamErrorEnvelope.Frame(
                BowireStreamErrorKinds.NotConfigured,
                "graphql-transport-ws subscriptions require the WebSocket plugin "
                + "(Kuestenlogik.Bowire.Protocol.WebSocket). Install it, or set the metadata header '"
                + SubscriptionTransportMetadataKey + "' to 'sse' to use graphql-sse instead.");
            yield break;
        }

        await foreach (var evt in StreamViaGraphQLTransportWsAsync(wsChannel, serverUrl, operation, variables, headers, ct, operationName))
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
    private async IAsyncEnumerable<string> StreamViaSseAsync(
        string serverUrl,
        string operation,
        JsonElement? variables,
        Dictionary<string, string>? headers,
        [EnumeratorCancellation] CancellationToken ct,
        string? operationName = null)
    {
        var endpoint = serverUrl.TrimEnd('/');
        var payload = variables.HasValue && variables.Value.ValueKind == JsonValueKind.Object
            ? (object)new { query = operation, variables = variables.Value, operationName }
            : new { query = operation, operationName };

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
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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
            yield return BowireStreamErrorEnvelope.Frame(
                BowireStreamErrorKinds.Transport, sendError ?? "graphql-sse send failed");
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
        [EnumeratorCancellation] CancellationToken ct,
        string? operationName = null)
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
            yield return BowireStreamErrorEnvelope.Frame(
                BowireStreamErrorKinds.Transport, connectError ?? "graphql-ws connect failed");
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
                // Unwrap text frames into their JSON-RPC payload. The
                // WebSocket plugin parses text frames into a JsonElement
                // when they're valid JSON (so the UI shows nested objects
                // instead of escaped strings) and falls back to a raw
                // string only on parse failure — handle both shapes.
                if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("type", out var envType) &&
                    envType.GetString() == "text" && msg.TryGetProperty("text", out var inner))
                {
                    if (inner.ValueKind == JsonValueKind.String)
                    {
                        var innerText = inner.GetString();
                        if (string.IsNullOrEmpty(innerText)) continue;
                        try { msg = JsonSerializer.Deserialize<JsonElement>(innerText); }
                        catch { continue; }
                    }
                    else if (inner.ValueKind == JsonValueKind.Object)
                    {
                        msg = inner.Clone();
                    }
                    else
                    {
                        continue;
                    }
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
                            ? (object)new { query = operation, variables = variables.Value, operationName }
                            : new { query = operation, operationName };
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
                        // #712 - the server ended the subscription itself.
                        // Its own errors array is the message, verbatim:
                        // summarising it here would lose the path and the
                        // extensions a GraphQL error carries.
                        yield return BowireStreamErrorEnvelope.Frame(
                            BowireStreamErrorKinds.Server,
                            msg.TryGetProperty("payload", out var errPayload)
                                ? JsonSerializer.Serialize(errPayload, s_indented)
                                : "graphql-ws error");
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

    /// <summary>
    /// The <c>operationName</c> the caller put in the payload, if any (#710).
    /// </summary>
    /// <remarks>
    /// This is how a caller with several operations in one document says
    /// which one it means. Nothing else can know: the document declares
    /// them all and the wire carries no other hint.
    /// </remarks>
    private static string? ExtractOperationName(List<string> jsonMessages)
    {
        if (jsonMessages.Count == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(jsonMessages[0]);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("operationName", out var n)
                && n.ValueKind == JsonValueKind.String)
            {
                var s = n.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }
        catch (JsonException)
        {
            // The caller's payload is not JSON; TryParseFullRequest has
            // already decided what that means.
        }
        return null;
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

    private async Task<(string Operation, JsonElement Variables)> BuildOperationAsync(
        string operationKind, string service, string method, List<string> jsonMessages,
        string? endpoint, bool mayIntrospect, CancellationToken ct)
    {
        var variablesJson = jsonMessages.Count > 0 ? jsonMessages[0] : "{}";

        // #710 - the discovered method carries the real argument types and
        // the real output type, so the generated operation both types its
        // variables correctly and selects actual fields instead of just
        // __typename.
        if (endpoint is not null)
        {
            var services = await SchemaForAsync(endpoint, mayIntrospect, ct);
            if (services is not null && FindMethod(services, service, method) is { } discovered)
                return GraphQLQueryBuilder.Build(operationKind, discovered, variablesJson);
        }

        // No schema for this endpoint — it refuses introspection, or is not
        // reachable, or is not GraphQL at all. /api/invoke does not pass the
        // method object back either, so re-derive what can be derived from
        // the body. The operation stays valid; its selection set falls back
        // to __typename because a stub has no output type to walk.
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
    /// The schema for an endpoint, introspecting it once if nobody has
    /// (#710).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Relying on a prior Discover would have made the generated selection
    /// set depend on which screen the operator had visited. A flow that
    /// names a service and a method directly never discovers anything, and
    /// flows are exactly the caller this change exists for.
    /// </para>
    /// <para>
    /// A failed introspection is remembered as an empty list rather than
    /// retried. A server that refuses introspection refuses it every time,
    /// and paying a round trip per invoke to be told so again is a cost the
    /// caller did not ask for.
    /// </para>
    /// <para>
    /// <paramref name="mayIntrospect"/> is false on the subscription path,
    /// and that is not caution — it is correctness. A graphql-sse
    /// subscription endpoint IS a POST to the same URL, so an introspection
    /// query sent there is answered as a subscription: an event stream that
    /// stays open. The client would then sit waiting for a JSON body that
    /// never ends. A subscription therefore uses the cache when a Discover
    /// has filled it and the stub otherwise.
    /// </para>
    /// </remarks>
    private async Task<List<BowireServiceInfo>?> SchemaForAsync(
        string endpoint, bool mayIntrospect, CancellationToken ct)
    {
        if (_discovered.TryGetValue(endpoint, out var cached))
            return cached.Count == 0 ? null : cached;

        if (!mayIntrospect) return null;

        var services = await DiscoverAsync(endpoint, showInternalServices: false, ct);
        // DiscoverAsync stores a non-empty result itself; record the empty
        // one here so the next call does not ask again.
        if (services.Count == 0) _discovered[endpoint] = [];
        return services.Count == 0 ? null : services;
    }

    /// <summary>
    /// The discovered method behind a service/method pair, or null.
    /// </summary>
    private static BowireMethodInfo? FindMethod(
        List<BowireServiceInfo> services, string service, string method)
    {
        foreach (var s in services)
        {
            if (!string.Equals(s.Name, service, StringComparison.Ordinal)) continue;
            foreach (var m in s.Methods)
            {
                if (string.Equals(m.Name, method, StringComparison.Ordinal)) return m;
            }
        }
        return null;
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

    private async Task<JsonElement> SendOperationAsync(
        string endpoint,
        string query,
        JsonElement? variables,
        Dictionary<string, string>? headers,
        CancellationToken ct,
        string? operationName = null,
        bool useGet = false)
    {
        using var request = useGet
            ? new HttpRequestMessage(HttpMethod.Get, BuildGetUri(endpoint, query, variables, operationName))
            : new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        if (headers is not null)
        {
            foreach (var (key, value) in headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        if (!useGet)
        {
            // #710 - operationName rides along when there is one to send.
            // The serializer is configured to drop nulls, so a
            // single-operation document still puts exactly the two fields
            // on the wire it always did, and no server sees a shape it did
            // not see before.
            var payload = variables.HasValue && variables.Value.ValueKind == JsonValueKind.Object
                ? (object)new { query, variables = variables.Value, operationName }
                : new { query, operationName };

            request.Content = JsonContent.Create(payload, options: s_jsonOptions);
        }

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(s_jsonOptions, ct);
    }

    /// <summary>
    /// What kind of operation a document actually declares (#713).
    /// </summary>
    /// <remarks>
    /// The service name says "Query" for a discovered method, but a verbatim
    /// document carries its own keyword and that is the one that counts —
    /// somebody can paste a mutation into a tab the rail thinks is a query.
    /// Falls back to the caller's expectation when the document declares
    /// nothing readable, which leaves the existing behaviour in place.
    /// </remarks>
    private static string OperationKindOf(string document, string fallback)
    {
        foreach (var definition in GraphQLDocumentInfo.OperationKinds(document))
            return definition;
        return fallback;
    }

    /// <summary>
    /// The URL for a query sent over <c>GET</c> (#713).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The GraphQL-over-HTTP convention: <c>query</c>, <c>variables</c> as
    /// a JSON string, and <c>operationName</c>, all percent-encoded. An
    /// endpoint that already carries a query string keeps it — some
    /// deployments route on one, and dropping it would send the request
    /// somewhere else entirely.
    /// </para>
    /// </remarks>
    internal static Uri BuildGetUri(
        string endpoint, string query, JsonElement? variables, string? operationName)
    {
        var parts = new List<string> { "query=" + Uri.EscapeDataString(query) };

        if (variables.HasValue && variables.Value.ValueKind == JsonValueKind.Object)
        {
            var json = JsonSerializer.Serialize(variables.Value, s_jsonOptions);
            // An empty object carries no information and only lengthens a
            // URL that is already the tightest budget on this path.
            if (json != "{}") parts.Add("variables=" + Uri.EscapeDataString(json));
        }

        if (!string.IsNullOrWhiteSpace(operationName))
            parts.Add("operationName=" + Uri.EscapeDataString(operationName));

        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return new Uri(endpoint + separator + string.Join("&", parts), UriKind.Absolute);
    }

    /// <summary>
    /// Whether this call was asked to go over <c>GET</c>, and the metadata
    /// with that instruction removed (#713).
    /// </summary>
    /// <remarks>
    /// Stripped from the metadata for the same reason the subscription
    /// transport key is: it is an instruction to Bowire, not a header the
    /// server should ever see.
    /// </remarks>
    private static (bool UseGet, Dictionary<string, string>? Headers) ExtractHttpMethod(
        Dictionary<string, string>? metadata)
    {
        if (metadata is null) return (false, null);

        string? matched = null;
        var useGet = false;
        foreach (var (k, v) in metadata)
        {
            if (!string.Equals(k, HttpMethodMetadataKey, StringComparison.OrdinalIgnoreCase)) continue;
            matched = k;
            useGet = string.Equals(v?.Trim(), "get", StringComparison.OrdinalIgnoreCase);
            break;
        }

        if (matched is null) return (false, metadata);

        var filtered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in metadata)
        {
            if (!string.Equals(k, matched, StringComparison.Ordinal)) filtered[k] = v;
        }
        return (useGet, filtered);
    }
}
