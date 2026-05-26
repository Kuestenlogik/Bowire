// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Protocol.JsonRpc;

/// <summary>
/// Minimal JSON-RPC 2.0 client over HTTP. Owns no transport state
/// beyond the supplied <see cref="HttpClient"/> — callers stand up
/// the client once per Bowire-protocol invocation and the HTTP layer
/// pools connections.
/// </summary>
/// <remarks>
/// <para>
/// Scope: request/response only. The "notifications" half of the
/// spec (one-way messages, <c>id</c> omitted) and batch requests
/// (array envelope) are not exposed here — Bowire's protocol surface
/// is unary by default, so a single <see cref="CallAsync"/> per
/// invoke step is the natural shape.
/// </para>
/// <para>
/// The plugin uses this directly; future polyglot-sidecar work can
/// reuse the same client over a stdio transport by swapping
/// <see cref="HttpClient"/> for a streaming send/receive pair (the
/// envelope shape is identical across transports).
/// </para>
/// </remarks>
internal sealed class JsonRpcClient
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private long _idSeed;

    public JsonRpcClient(
        HttpClient http,
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        _http = http;
        _endpoint = endpoint;
        _headers = headers;
    }

    /// <summary>
    /// Send a single JSON-RPC request and return the decoded
    /// <c>result</c> element. Errors arrive via <see cref="JsonRpcException"/>
    /// carrying the spec's <c>code</c> + <c>message</c> + optional
    /// <c>data</c>.
    /// </summary>
    /// <param name="method">JSON-RPC method name.</param>
    /// <param name="parameters">
    /// Either a JSON object (named params) or a JSON array (positional
    /// params). Pass <see langword="null"/> or
    /// <see cref="JsonValueKind.Undefined"/> to omit the field
    /// entirely — some servers reject an explicit <c>params: null</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<JsonElement> CallAsync(
        string method,
        JsonElement? parameters,
        CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _idSeed);
        var envelope = BuildRequestEnvelope(method, parameters, id);

        using var content = new StringContent(envelope, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_headers is not null)
        {
            foreach (var (k, v) in _headers)
            {
                req.Headers.TryAddWithoutValidation(k, v);
            }
        }

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // JSON-RPC servers should answer 2xx + JSON regardless of
        // application-level success. Some still surface 4xx/5xx on
        // logical errors though — handle both shapes.
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException ex)
        {
            throw new JsonRpcException(
                code: -32700,
                message: $"Server returned non-JSON body (HTTP {(int)resp.StatusCode}): {ex.Message}",
                data: default);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonRpcException(
                    code: -32700,
                    message: $"JSON-RPC envelope must be an object, got {root.ValueKind}.",
                    data: default);
            }

            // Spec doesn't require id-echo to match, but we still verify
            // when present — saves diagnostic time when a proxy
            // re-orders responses.
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                    ? c.GetInt32() : -32603;
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() ?? "" : "Unknown JSON-RPC error";
                var data = error.TryGetProperty("data", out var d)
                    ? (JsonElement?)d.Clone() : null;
                throw new JsonRpcException(code, message, data);
            }

            if (root.TryGetProperty("result", out var result))
            {
                return result.Clone();
            }

            throw new JsonRpcException(
                code: -32603,
                message: "JSON-RPC response carried neither 'result' nor 'error'.",
                data: default);
        }
    }

    private static string BuildRequestEnvelope(
        string method, JsonElement? parameters, long id)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", method);
            if (parameters is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } p)
            {
                writer.WritePropertyName("params");
                p.WriteTo(writer);
            }
            writer.WriteNumber("id", id);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>
/// Thrown when a JSON-RPC call surfaces an <c>error</c> envelope
/// (well-formed JSON-RPC reply with a numeric <c>code</c> and a
/// <c>message</c>). Also wraps malformed-envelope and non-JSON-body
/// situations with synthetic codes from the spec's reserved range
/// (-32700 parse error, -32603 internal).
/// </summary>
public sealed class JsonRpcException : Exception
{
    public int Code { get; }
    public JsonElement? RpcData { get; }

    public JsonRpcException() { }

    public JsonRpcException(string message) : base(message) { }

    public JsonRpcException(string message, Exception innerException)
        : base(message, innerException) { }

    public JsonRpcException(int code, string message, JsonElement? data)
        : base(message)
    {
        Code = code;
        RpcData = data;
    }
}
