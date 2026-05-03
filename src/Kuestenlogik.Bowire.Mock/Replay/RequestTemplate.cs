// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.Mock.Replay;

/// <summary>
/// Snapshot of the inbound HTTP request that response-body substitution
/// can project values out of at replay time. Lets recorded response
/// payloads reference the live request via <c>${request.*}</c> tokens so
/// a single recording can power many test cases — think echo endpoints
/// or mocks that need to mirror a correlation id back to the caller.
/// </summary>
/// <remarks>
/// <para>Supported token shape:</para>
/// <list type="bullet">
///   <item><c>${request.method}</c> — HTTP verb.</item>
///   <item><c>${request.path}</c> — full request path.</item>
///   <item><c>${request.path.N}</c> — 0-based path segment
///   (<c>/a/b/c</c> → <c>0=a, 1=b, 2=c</c>).</item>
///   <item><c>${request.path.NAME}</c> — captured value of the
///   <c>{NAME}</c> placeholder in the matched step's
///   <c>httpPath</c> template, when the matcher compiled one.</item>
///   <item><c>${request.query.NAME}</c> — query value (repeat params
///   return the first occurrence; case-insensitive lookup).</item>
///   <item><c>${request.header.NAME}</c> — request header value
///   (case-insensitive).</item>
///   <item><c>${request.body}</c> — raw request body as a string
///   (read once and cached).</item>
///   <item><c>${request.body.a.b.c}</c> — JSON-path navigation into
///   the parsed request body. Dots step into object properties; integer
///   segments step into JSON arrays (<c>${request.body.items.0.id}</c>).</item>
/// </list>
/// <para>
/// Unknown tokens fall through to the substitutor's "leave literal"
/// behaviour so a <c>${request.whatever}</c> in a recorded body stays
/// as-is rather than getting silently swallowed.
/// </para>
/// </remarks>
public sealed class RequestTemplate
{
    private readonly HttpContext _ctx;
    private readonly IReadOnlyDictionary<string, string>? _templateBindings;
    private readonly string? _body;
    private JsonDocument? _parsedBody;
    private bool _triedParseBody;

    /// <summary>
    /// Build a template from the live <paramref name="ctx"/>. Pass the
    /// already-read request body as <paramref name="body"/> — the caller
    /// is in a better position to buffer it once than the template is
    /// (the substitutor can't re-read the stream mid-pipeline).
    /// Path-template bindings come from the matcher's regex capture
    /// when the matched step's <c>httpPath</c> contained
    /// <c>{name}</c> placeholders; pass <c>null</c> otherwise.
    /// </summary>
    public RequestTemplate(
        HttpContext ctx,
        string? body,
        IReadOnlyDictionary<string, string>? templateBindings)
    {
        _ctx = ctx;
        _body = body;
        _templateBindings = templateBindings;
    }

    /// <summary>
    /// Resolve a <c>request.*</c> sub-token. Returns <c>null</c> when
    /// the token doesn't address anything the template can reach; the
    /// substitutor treats that as "leave literal" so the final output
    /// still contains the original placeholder text.
    /// </summary>
    public string? Resolve(string subToken)
    {
        if (string.Equals(subToken, "method", StringComparison.Ordinal))
            return _ctx.Request.Method;

        if (string.Equals(subToken, "path", StringComparison.Ordinal))
            return _ctx.Request.Path.Value ?? "/";

        if (string.Equals(subToken, "body", StringComparison.Ordinal))
            return _body;

        if (subToken.StartsWith("path.", StringComparison.Ordinal))
            return ResolvePathSegment(subToken[5..]);

        if (subToken.StartsWith("query.", StringComparison.Ordinal))
            return ResolveQuery(subToken[6..]);

        if (subToken.StartsWith("header.", StringComparison.Ordinal))
            return ResolveHeader(subToken[7..]);

        if (subToken.StartsWith("body.", StringComparison.Ordinal))
            return ResolveBodyPath(subToken[5..]);

        return null;
    }

    private string? ResolvePathSegment(string segmentToken)
    {
        // Numeric index → positional segment. Non-numeric → named
        // template binding. Both live under the same prefix so
        // `${request.path.0}` and `${request.path.id}` read naturally.
        if (int.TryParse(segmentToken, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var index) &&
            index >= 0)
        {
            var path = _ctx.Request.Path.Value ?? "";
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return index < segments.Length ? segments[index] : null;
        }

        if (_templateBindings is not null &&
            _templateBindings.TryGetValue(segmentToken, out var bound))
        {
            return bound;
        }
        return null;
    }

    private string? ResolveQuery(string name)
    {
        if (!_ctx.Request.Query.TryGetValue(name, out var value))
        {
            // Case-insensitive fallback — query params are commonly
            // treated as case-insensitive in practice even though the
            // URI spec leaves it undefined.
            foreach (var kv in _ctx.Request.Query)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value;
                    break;
                }
            }
        }
        return value.Count > 0 ? value[0] : null;
    }

    private string? ResolveHeader(string name)
    {
        // Request.Headers is case-insensitive; one direct lookup covers
        // both "Authorization" and "authorization".
        if (_ctx.Request.Headers.TryGetValue(name, out var value) && value.Count > 0)
        {
            return value[0];
        }
        return null;
    }

    private string? ResolveBodyPath(string path)
    {
        if (_body is null) return null;

        if (!_triedParseBody)
        {
            _triedParseBody = true;
            try
            {
                _parsedBody = JsonDocument.Parse(_body);
            }
            catch (JsonException)
            {
                _parsedBody = null;
            }
        }
        if (_parsedBody is null) return null;

        var element = _parsedBody.RootElement;
        foreach (var segment in path.Split('.'))
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (!element.TryGetProperty(segment, out var next)) return null;
                element = next;
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(segment, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var idx) ||
                    idx < 0 || idx >= element.GetArrayLength())
                {
                    return null;
                }
                element = element[idx];
            }
            else
            {
                return null;
            }
        }

        // Final leaf: render scalars as their string form, objects and
        // arrays as compact JSON so the output is at least copy-
        // pasteable if the user embeds a whole sub-tree.
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null => "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => element.GetRawText(),
            _ => element.GetRawText()
        };
    }
}
