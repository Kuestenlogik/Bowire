// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// HTTP binding for AsyncAPI documents — the only AsyncAPI binding
/// that doesn't need a separate wire plugin loaded. The resolver
/// drives <see cref="HttpClient"/> directly: AsyncAPI <c>send</c>
/// becomes an outgoing HTTP request, <c>receive</c> models a
/// one-shot reply.
///
/// Why not route through the REST plugin? The REST plugin's
/// <c>InvokeAsync</c> is schema-driven (it looks up the call in an
/// OpenAPI document and shapes the request from the operation
/// definition). AsyncAPI documents declare HTTP bindings without
/// any OpenAPI overlay — the binding fields (<c>method</c>,
/// <c>query</c>, <c>headers</c>, <c>type</c>) and the channel
/// address are everything the resolver gets. Building the request
/// in-line is simpler and side-steps the schema constraint.
///
/// Binding fields the AsyncAPI HTTP spec defines:
///   - operation-level: <c>type</c> (request|response),
///     <c>method</c> (GET / POST / PUT / DELETE / …),
///     <c>query</c> (JSON Schema for query params),
///     <c>headers</c> (JSON Schema for headers).
///   - channel-level: same fields, kept for backwards compat.
///   - message-level: <c>headers</c>, <c>statusCode</c> — not
///     consumed here (would need per-message resolution that
///     AsyncApiBindingsExtractor doesn't surface yet).
///
/// The resolver consumes <c>method</c> verbatim (defaults to POST
/// for <c>send</c> + GET for <c>receive</c>), takes the channel
/// address as the path joined onto the server URL, and treats the
/// first JSON message as the request body (for verbs that carry
/// one). Other binding fields ride along the merged metadata bag
/// so a recorder / mock can capture them.
/// </summary>
internal sealed class HttpBindingResolver : IAsyncApiBindingResolver
{
    public string BindingId => "http";

    public BowireMethodInfo BuildMethod(AsyncApiChannelContext channel)
    {
        throw new NotImplementedException(
            "HttpBindingResolver.BuildMethod is reserved for a later phase " +
            "where per-binding method metadata (HTTP verb, declared query / " +
            "header schemas) needs to surface on the method itself. The " +
            "current phase builds methods directly from the V3/V2 " +
            "operation block.");
    }

    public async Task<InvokeResult> InvokeAsync(
        AsyncApiChannelContext channel, List<string> jsonMessages,
        Dictionary<string, string>? metadata, CancellationToken ct)
    {
        // Resolve verb: doc binding wins, caller metadata can override,
        // fall back to action-derived default (send → POST, receive → GET).
        var verb = ResolveHttpMethod(channel, metadata);
        var url = JoinServerAndChannel(channel.ServerUrl, channel.ChannelAddress);
        var body = jsonMessages.FirstOrDefault();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var http = new HttpClient();
            using var request = new HttpRequestMessage(verb, url);

            // Body only for verbs that carry one. AsyncAPI doc may
            // declare a content-type via bindings.http.headers; the
            // resolver defaults to application/json since AsyncAPI
            // payloads are JSON Schema-typed.
            if (body is not null && HttpMethodHasBody(verb))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            // Forward caller-metadata + binding headers as request
            // headers — anything that isn't a reserved-by-Bowire
            // marker (mTLS, transport, etc.) lands on the request.
            ApplyHeadersFromMetadata(request, metadata);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            sw.Stop();

            return new InvokeResult(
                Response: responseBody,
                DurationMs: sw.ElapsedMilliseconds,
                Status: response.IsSuccessStatusCode ? "OK" : $"HTTP {(int)response.StatusCode}",
                Metadata: new Dictionary<string, string>
                {
                    ["http.method"] = verb.Method,
                    ["http.url"] = url,
                    ["http.status"] = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return new InvokeResult(
                Response: null,
                DurationMs: sw.ElapsedMilliseconds,
                Status: "Error",
                Metadata: new Dictionary<string, string>
                {
                    ["error"] = ex.Message,
                    ["http.method"] = verb.Method,
                    ["http.url"] = url
                });
        }
    }

    /// <summary>
    /// Pick the HTTP verb: doc binding (bindings.http.method) wins,
    /// caller metadata can override on a per-call basis, fall back
    /// to action-derived default. AsyncAPI's <c>type</c> field
    /// (<c>request</c>/<c>response</c>) is informational here; the
    /// <c>method</c> field carries the actual verb.
    /// </summary>
    private static HttpMethod ResolveHttpMethod(
        AsyncApiChannelContext channel, Dictionary<string, string>? metadata)
    {
        string? verbName = null;
        if (metadata is not null && metadata.TryGetValue("method", out var callerVerb))
        {
            verbName = callerVerb;
        }
        else if (channel.BindingFields.TryGetValue("method", out var docVerb))
        {
            verbName = docVerb;
        }

        if (!string.IsNullOrWhiteSpace(verbName))
        {
            return new HttpMethod(verbName.ToUpperInvariant());
        }

        // No verb declared → default by AsyncAPI action.
        return string.Equals(channel.OperationAction, "receive", StringComparison.OrdinalIgnoreCase)
            ? HttpMethod.Get
            : HttpMethod.Post;
    }

    /// <summary>
    /// Join the AsyncAPI <c>servers[]</c> URL with the channel
    /// address. Channel address is a relative path (it can carry a
    /// leading slash or not). Server URL may already include a base
    /// path. Trim the join point so we don't end up with double or
    /// missing slashes.
    /// </summary>
    private static string JoinServerAndChannel(string serverUrl, string channelAddress)
    {
        if (string.IsNullOrEmpty(channelAddress)) return serverUrl;
        var trimmedServer = serverUrl.TrimEnd('/');
        var trimmedChannel = channelAddress.TrimStart('/');
        return $"{trimmedServer}/{trimmedChannel}";
    }

    /// <summary>
    /// Verbs that AsyncAPI's HTTP binding allows carrying a request
    /// body. GET, HEAD, DELETE, OPTIONS bodies are technically legal
    /// but commonly stripped by intermediaries; AsyncAPI authors who
    /// want one should declare POST/PUT/PATCH explicitly via the
    /// <c>method</c> field.
    /// </summary>
    private static bool HttpMethodHasBody(HttpMethod method) =>
        method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch;

    /// <summary>
    /// Apply caller-metadata as HTTP headers. Skips internal markers
    /// the rest of Bowire reserves (mTLS, transport, etc.) — those
    /// would otherwise leak onto the wire request as literal HTTP
    /// headers. Also skips the binding fields the resolver consumes
    /// directly (<c>method</c>, <c>type</c>), which would confuse a
    /// downstream server if echoed as a header.
    /// </summary>
    private static void ApplyHeadersFromMetadata(
        HttpRequestMessage request, Dictionary<string, string>? metadata)
    {
        if (metadata is null) return;
        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (key.StartsWith("X-Bowire-", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.StartsWith("__bowire", StringComparison.Ordinal)) continue;
            if (string.Equals(key, "method", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(key, "type", StringComparison.OrdinalIgnoreCase)) continue;

            // Try request headers first; fall back to content
            // headers (Content-Type, Content-Length, etc.) which
            // HttpClient routes through a different bucket.
            if (!request.Headers.TryAddWithoutValidation(key, value))
            {
                request.Content?.Headers.TryAddWithoutValidation(key, value);
            }
        }
    }
}
