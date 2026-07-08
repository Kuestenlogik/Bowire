// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Protocol probe for GraphQL, rolling up to <c>API4:2023 — Unrestricted
/// Resource Consumption</c>. This is the query-<em>depth</em> sibling of the
/// alias-batching <see cref="GraphQLResourceLimitProbe"/>: where that probe
/// measures breadth (one field aliased thousands of times), this one measures
/// nesting. A deeply nested query forces the server into expensive recursive
/// resolution — each level multiplies the work of the level below it — so a
/// server with no maximum-depth rule can be driven to exhaust CPU / memory with
/// a single small request, the depth analog of the breadth amplification lever.
///
/// <para>Black-box and non-destructive. The probe first sends a trivial
/// <c>{ __typename }</c> query to confirm the endpoint speaks GraphQL, then
/// confirms introspection is enabled (the schema-independent depth vector nests
/// the introspection <c>ofType</c> chain, which only resolves when introspection
/// is on). It then sends ONE query nested to depth
/// <see cref="MaxDepth"/> through <c>ofType</c>. A server that resolves the whole
/// nesting has no depth cap (Vulnerable); one that rejects it with a validation /
/// depth error — or a 400 / 413 / 429 — enforces a limit (Safe). Not-GraphQL,
/// introspection-disabled, or unreachable targets skip rather than reporting a
/// false pass — introspection-disabled is itself a good production state, just
/// not testable for depth by this schema-independent probe.</para>
/// </summary>
internal sealed class GraphQLQueryDepthProbe : IOwaspProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API4:2023");

    public string ProtocolId => "graphql";

    /// <summary>
    /// Nesting depth of the amplification query. Kept deliberately modest:
    /// large enough to exceed a configured maximum-depth rule (most limiters cap
    /// well below this — Apollo / Hot Chocolate defaults live in the low teens),
    /// small enough that the query itself stays a harmless request — this detects
    /// the absence of a limit, it does not try to exhaust the target.
    /// </summary>
    private const int MaxDepth = 15;

    // Signals in a failed-invocation status string that mean the server actively
    // *rejected* the deeply nested query (a limit fired) rather than the
    // transport simply falling over. Checked case-insensitively.
    private static readonly string[] s_rejectionSignals =
    [
        "400", "413", "429", "complex", "depth", "limit", "too many", "too large", "cost",
    ];

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct)
    {
        // 1. Preflight: is this actually a GraphQL endpoint? `{ __typename }` is
        //    the cheapest possible valid query and returns data on any GraphQL
        //    server. Auth headers ride along so an authenticated API answers the
        //    preflight instead of bouncing it.
        var metadata = ToMetadata(authHeaders);
        InvokeResult preflight;
        try
        {
            preflight = await protocol.InvokeAsync(target, "Query", "__typename",
                [Query("{ __typename }")], showInternalServices: false, metadata, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker(ScanFindingStatus.Skipped, "API4-GRAPHQL-DEPTH-UNREACHABLE", "GraphQL query-depth probe skipped",
                $"The GraphQL preflight query could not be sent ({ex.GetType().Name}).")];
        }

        if (!LooksLikeGraphQl(preflight))
        {
            return [Marker(ScanFindingStatus.Skipped, "API4-GRAPHQL-DEPTH-NOT-GRAPHQL", "GraphQL query-depth probe skipped",
                "A `{ __typename }` preflight did not return a GraphQL data envelope — the target is not a GraphQL endpoint, or it is unreachable.")];
        }

        // 2. Introspection gate: the schema-independent depth vector nests the
        //    introspection `ofType` chain, which only resolves when introspection
        //    is enabled. Confirm `__schema` answers before nesting through it.
        InvokeResult introspection;
        try
        {
            introspection = await protocol.InvokeAsync(target, "Query", "introspectionProbe",
                [Query("{ __schema { queryType { name } } }")], showInternalServices: false, metadata, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker(ScanFindingStatus.Skipped, "API4-GRAPHQL-DEPTH-NO-INTROSPECTION", "GraphQL query-depth probe skipped",
                $"The introspection preflight could not be sent ({ex.GetType().Name}) — the schema-independent depth test cannot run.")];
        }

        if (!IntrospectionEnabled(introspection))
        {
            return [Marker(ScanFindingStatus.Skipped, "API4-GRAPHQL-DEPTH-NO-INTROSPECTION", "GraphQL query-depth probe skipped",
                "Introspection is disabled (no `data.__schema` in the introspection response), so this schema-independent depth test — which nests the introspection `ofType` chain — cannot run. Introspection-disabled is a good production state; it is simply not testable for query depth here, not a pass.")];
        }

        // 3. Amplification: one request nested to depth MaxDepth through `ofType`.
        InvokeResult deep;
        try
        {
            deep = await protocol.InvokeAsync(target, "Query", "deepQuery",
                [Query(BuildDeepQuery(MaxDepth))], showInternalServices: false, metadata, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A rejected query (non-2xx) surfaces as a null-response InvokeResult
            // *or* an exception depending on the transport error; treat a message
            // that names a rejection status as "limit enforced".
            return [ClassifyByStatus(ex.Message)];
        }

        return [ClassifyDeep(deep)];
    }

    /// <summary>Decide the verdict from the deeply nested response.</summary>
    private ScanFinding ClassifyDeep(InvokeResult deep)
    {
        // A null response means the plugin caught a non-2xx (the GraphQL plugin
        // maps EnsureSuccessStatusCode failures to Response=null, Status=message).
        // Preflight already proved the endpoint reachable + GraphQL, so a refusal
        // of the *deep* query is the server enforcing a limit.
        if (deep.Response is null)
            return ClassifyByStatus(deep.Status);

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(deep.Response);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Marker(ScanFindingStatus.Skipped, "API4-GRAPHQL-DEPTH-INCONCLUSIVE", "GraphQL query-depth probe inconclusive",
                "The deep-query response was not JSON — the limit could not be determined.");
        }

        // A GraphQL `errors` envelope (HTTP 200 with { "errors": [...] }) is the
        // canonical "query rejected" shape — validation / depth limits report
        // here. That is the desired production state.
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            return Marker(ScanFindingStatus.Safe, "API4-GRAPHQL-DEPTH-LIMIT-ENFORCED", "GraphQL query-depth limit enforced",
                $"A query nested to depth {MaxDepth} was rejected with a GraphQL error — the server enforces a maximum query-depth rule.");
        }

        // Data came back for the whole depth-15 nesting → no depth cap.
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object)
        {
            return Finding("BWR-OWASP-API4-GRAPHQL-DEPTH", "GraphQL has no query-depth limit",
                $"A query nested to depth {MaxDepth} was executed in full — the server enforces no maximum query depth. Deeply nested queries force expensive recursive resolution (each level multiplies the work below it), a resource-exhaustion / denial-of-service lever and the depth analog of the alias-batching breadth limit.",
                "Enforce a maximum query depth AND a complexity / cost limit before execution. Apollo Server: `graphql-depth-limit` + cost plugins; Hot Chocolate: `.AddMaxExecutionDepthRule(n)` + cost analysis; graphql-ruby: `max_depth` / `max_complexity`. Also apply a request-body size cap at the gateway.",
                "medium", 5.3);
        }

        return Marker(ScanFindingStatus.Skipped, "API4-GRAPHQL-DEPTH-INCONCLUSIVE", "GraphQL query-depth probe inconclusive",
            "The deep-query response carried neither a data object nor an errors array — the limit could not be determined.");
    }

    /// <summary>
    /// Classify a failed / non-2xx invocation by its status string: a status
    /// that names a rejection code (400 / 413 / 429) or a limit keyword means
    /// the server refused the deep query (Safe); anything else is a transport
    /// wobble we can't read a verdict from (Skip).
    /// </summary>
    private ScanFinding ClassifyByStatus(string? status)
    {
        var s = status ?? "";
        if (s_rejectionSignals.Any(sig => s.Contains(sig, StringComparison.OrdinalIgnoreCase)))
        {
            return Marker(ScanFindingStatus.Safe, "API4-GRAPHQL-DEPTH-LIMIT-ENFORCED", "GraphQL query-depth limit enforced",
                $"The server refused a query nested to depth {MaxDepth} ({s}) — a maximum query-depth / request-size limit is enforced.");
        }

        return Marker(ScanFindingStatus.Skipped, "API4-GRAPHQL-DEPTH-INCONCLUSIVE", "GraphQL query-depth probe inconclusive",
            $"The deep query did not complete ({s}) — the limit could not be determined.");
    }

    /// <summary>
    /// True when a preflight response looks like a GraphQL data envelope
    /// (<c>{ "data": { "__typename": ... } }</c>). Guards against classifying a
    /// non-GraphQL endpoint's response as "no limit".
    /// </summary>
    private static bool LooksLikeGraphQl(InvokeResult preflight)
    {
        if (preflight.Response is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(preflight.Response);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("__typename", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the introspection response is a data envelope carrying a
    /// <c>__schema</c> object (<c>{ "data": { "__schema": { ... } } }</c>). An
    /// errors envelope, a null response, or a missing <c>__schema</c> all mean
    /// introspection is disabled — the schema-independent depth vector needs it.
    /// </summary>
    private static bool IntrospectionEnabled(InvokeResult introspection)
    {
        if (introspection.Response is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(introspection.Response);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("__schema", out var schema)
                && schema.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Build a schema-independent, depth-<paramref name="depth"/> introspection
    /// query by nesting <c>ofType</c> chains:
    /// <c>{ __schema { types { fields { type { ofType { … name } } } } } }</c>.
    /// Opens the fixed prologue, emits <paramref name="depth"/> copies of
    /// <c>ofType {</c>, then the leaf <c>name</c>, then closes every brace so the
    /// result is always balanced.
    /// </summary>
    private static string BuildDeepQuery(int depth)
    {
        var sb = new StringBuilder(depth * 10);
        // Prologue: { __schema { types { fields { type   (4 opening braces)
        sb.Append("{ __schema { types { fields { type ");
        for (var i = 0; i < depth; i++)
        {
            sb.Append("ofType { ");
        }
        sb.Append("name ");
        // Close: depth `ofType` braces + the 4 prologue braces.
        for (var i = 0; i < depth + 4; i++)
        {
            sb.Append("} ");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Wrap a raw GraphQL operation in the plugin's full-request JSON shape.</summary>
    private static string Query(string operation)
        => JsonSerializer.Serialize(new { query = operation });

    /// <summary>
    /// Fold the scan's <c>--auth-header</c> values (<c>Name: Value</c> strings)
    /// into a metadata dictionary the plugin forwards as request headers, so an
    /// authenticated GraphQL API answers the probe instead of bouncing it.
    /// </summary>
    private static Dictionary<string, string>? ToMetadata(IList<string> authHeaders)
    {
        if (authHeaders is null || authHeaders.Count == 0) return null;
        var md = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in authHeaders.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            var colon = raw.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var name = raw[..colon].Trim();
            var value = raw[(colon + 1)..].TrimStart();
            if (name.Length > 0) md[name] = value;
        }
        return md.Count > 0 ? md : null;
    }

    // ---- finding factories ----

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-770", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the GraphQL query-depth probe."),
        Status = status,
        Detail = detail,
    };
}
