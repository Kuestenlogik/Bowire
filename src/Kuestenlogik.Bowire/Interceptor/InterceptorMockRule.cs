// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Interceptor;

/// <summary>
/// One mock-injection rule (#308, Phase D). When the interceptor sees a
/// request whose path + method match this rule's <see cref="PathPattern"/>
/// + <see cref="Method"/>, it short-circuits the pipeline and serves the
/// rule's response directly — the host's endpoint never runs.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the recorded-mock surface (#36): the workbench's Mocks rail
/// already knows how to express "this method + this body returns this
/// status + this body". A mock rule is the same shape addressed by a
/// route pattern rather than a discovered service/method pair — it can
/// be authored from scratch in the Intercepted rail or seeded from an
/// existing recording step.
/// </para>
/// <para>
/// The pattern grammar is deliberately small: literal path-prefix match
/// against the request path. A trailing <c>/*</c> is treated as a
/// wildcard tail. Method may be the literal verb or <c>*</c> to match
/// any method. This is enough for Phase D — full regex matching is left
/// to a script-sandbox hook (#126) where the operator has the full
/// language available.
/// </para>
/// </remarks>
public sealed class InterceptorMockRule
{
    /// <summary>
    /// Stable id (URL-safe, monotonic) the workbench uses to address the
    /// rule for edit / delete. Assigned by
    /// <see cref="InterceptorMockStore"/> on insert.
    /// </summary>
    public string Id { get; init; } = "";

    /// <summary>
    /// Human-readable label that appears next to the rule in the rail.
    /// Defaults to "<see cref="Method"/> <see cref="PathPattern"/>" when
    /// the caller leaves it blank.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Path-prefix or wildcard pattern matched against the request path
    /// (e.g. <c>/api/users</c>, <c>/api/users/*</c>, <c>*</c>). The match
    /// is case-insensitive — request paths typically arrive normalised
    /// from ASP.NET routing.
    /// </summary>
    public string PathPattern { get; init; } = "*";

    /// <summary>
    /// HTTP method to match (e.g. <c>GET</c>) or <c>*</c> for any. Match
    /// is case-insensitive.
    /// </summary>
    public string Method { get; init; } = "*";

    /// <summary>
    /// HTTP status code the mock returns to the client. Defaults to 200.
    /// </summary>
    public int ResponseStatus { get; init; } = 200;

    /// <summary>
    /// Response headers the mock emits. <c>Content-Type</c> defaults to
    /// <c>application/json</c> when none is supplied — matches the
    /// 90%-case for REST mocks; the operator can override to plain text,
    /// XML, &amp;c.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders { get; init; }
        = Array.Empty<KeyValuePair<string, string>>();

    /// <summary>
    /// Response body as UTF-8 text. Null serves an empty body.
    /// </summary>
    public string? ResponseBody { get; init; }

    /// <summary>
    /// When set, the response body is base64-decoded before writing —
    /// the path for binary mocks (e.g. a recorded image response). When
    /// both <see cref="ResponseBody"/> and <see cref="ResponseBodyBase64"/>
    /// are set, the base64 wins.
    /// </summary>
    public string? ResponseBodyBase64 { get; init; }

    /// <summary>
    /// Optional artificial latency (milliseconds) the middleware sleeps
    /// before writing the mocked response — useful for reproducing a
    /// slow upstream when testing client retry / timeout behaviour.
    /// </summary>
    public int DelayMs { get; init; }

    /// <summary>
    /// When <see langword="false"/>, the rule is kept in the store but
    /// not consulted by the matcher. The workbench surface uses this to
    /// pause a rule without losing the definition.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Does this rule match the supplied method + path? Case-insensitive
    /// across both. <see cref="PathPattern"/> supports <c>*</c> as the
    /// "match any path" sentinel and a trailing <c>/*</c> as the
    /// "any tail" wildcard.
    /// </summary>
    public bool Matches(string method, string path)
    {
        if (!Enabled) return false;
        if (!string.Equals(Method, "*", StringComparison.Ordinal)
            && !string.Equals(Method, method, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return PathMatches(PathPattern, path);
    }

    internal static bool PathMatches(string pattern, string path)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        if (pattern == "*") return true;

        // Strip the query string from the path so /api/users?role=admin
        // still matches a /api/users pattern. Query-aware matching can
        // ride the script-sandbox hook (#126) where the operator has
        // the full request shape available.
        var q = path.IndexOf('?', StringComparison.Ordinal);
        var p = q < 0 ? path : path[..q];

        if (pattern.EndsWith("/*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^2];
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                // Either exact prefix or prefix + "/" + tail.
                if (p.Length == prefix.Length) return true;
                if (p.Length > prefix.Length && p[prefix.Length] == '/') return true;
            }
            return false;
        }

        return string.Equals(pattern, p, StringComparison.OrdinalIgnoreCase);
    }
}
