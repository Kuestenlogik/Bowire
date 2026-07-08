// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Protocol probe for GraphQL, rolling up to <c>API4:2023 — Unrestricted
/// Resource Consumption</c>. GraphQL's single endpoint lets a client fan a
/// query out arbitrarily: aliasing the same field thousands of times in one
/// request multiplies the server's resolver work while the request stays a
/// single small HTTP call (the classic "alias batching" amplification). A
/// server with no query-complexity / alias limit resolves every alias — a
/// direct denial-of-service lever and the GraphQL analog of the REST rate-limit
/// gap the HTTP <see cref="Api4ResourceProbe"/> looks for.
///
/// <para>Black-box and non-destructive. The probe first sends a trivial
/// <c>{ __typename }</c> query to confirm the endpoint actually speaks GraphQL,
/// then sends ONE query that aliases the always-available <c>__typename</c>
/// meta-field <see cref="AliasBatchSize"/> times. It is a detection probe, not
/// an exploit: the batch is deliberately bounded (a few hundred aliases — enough
/// to trip any sane complexity budget, small enough that it can't itself harm a
/// target). A server that resolves the whole batch has no cap (Vulnerable); one
/// that rejects it with a validation / complexity error — or a 400 / 413 / 429 —
/// enforces a limit (Safe). Not-GraphQL or unreachable targets skip rather than
/// reporting a false pass.</para>
/// </summary>
internal sealed class GraphQLResourceLimitProbe : IOwaspProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API4:2023");

    public string ProtocolId => "graphql";

    /// <summary>
    /// Number of <c>__typename</c> aliases in the amplification query. Kept
    /// deliberately modest: large enough to exceed a configured alias /
    /// complexity budget (most limiters cap well below this), small enough that
    /// the query itself stays a harmless ~few-KB request — this detects the
    /// absence of a limit, it does not try to exhaust the target.
    /// </summary>
    private const int AliasBatchSize = 500;

    // Signals in a failed-invocation status string that mean the server
    // actively *rejected* the oversized query (a limit fired) rather than the
    // transport simply falling over. Checked case-insensitively.
    private static readonly string[] s_rejectionSignals =
    [
        "400", "413", "429", "complex", "depth", "limit", "too many", "too large", "cost",
    ];

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct)
    {
        // 1. Preflight: is this actually a GraphQL endpoint? `{ __typename }` is
        //    the cheapest possible valid query and returns data on any GraphQL
        //    server, even one with introspection disabled. Auth headers ride
        //    along so an authenticated API answers the preflight instead of
        //    bouncing it — the check is about the *limit*, not about auth.
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
            return [Marker(ScanFindingStatus.Skipped, "API4-GRAPHQL-UNREACHABLE", "GraphQL resource-limit probe skipped",
                $"The GraphQL preflight query could not be sent ({ex.GetType().Name}).")];
        }

        if (!LooksLikeGraphQl(preflight))
        {
            return [Marker(ScanFindingStatus.Skipped, "API4-GRAPHQL-NOT-GRAPHQL", "GraphQL resource-limit probe skipped",
                "A `{ __typename }` preflight did not return a GraphQL data envelope — the target is not a GraphQL endpoint, or it is unreachable.")];
        }

        // 2. Amplification: one request, many aliases of the same meta-field.
        InvokeResult batch;
        try
        {
            batch = await protocol.InvokeAsync(target, "Query", "aliasBatch",
                [Query(BuildAliasBatchQuery(AliasBatchSize))], showInternalServices: false, metadata, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The plugin surfaces a rejected query (non-2xx) as a null-response
            // InvokeResult *or* an exception depending on the transport error;
            // treat a message that names a rejection status as "limit enforced".
            return [ClassifyByStatus(ex.Message)];
        }

        return [ClassifyBatch(batch)];
    }

    /// <summary>Decide the verdict from the amplification response.</summary>
    private ScanFinding ClassifyBatch(InvokeResult batch)
    {
        // A null response means the plugin caught a non-2xx (the GraphQL plugin
        // maps EnsureSuccessStatusCode failures to Response=null, Status=message).
        // Preflight already proved the endpoint reachable + GraphQL, so a refusal
        // of the *oversized* query is the server enforcing a limit.
        if (batch.Response is null)
            return ClassifyByStatus(batch.Status);

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(batch.Response);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Marker(ScanFindingStatus.Skipped, "API4-GRAPHQL-INCONCLUSIVE", "GraphQL resource-limit probe inconclusive",
                "The amplification response was not JSON — the limit could not be determined.");
        }

        // A GraphQL `errors` envelope (HTTP 200 with { "errors": [...] }) is the
        // canonical "query rejected" shape — validation / complexity / alias
        // limits report here. That is the desired production state.
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            return Marker(ScanFindingStatus.Safe, "API4-GRAPHQL-LIMIT-ENFORCED", "GraphQL query-complexity limit enforced",
                $"A single query aliasing __typename {AliasBatchSize} times was rejected with a GraphQL error — the server enforces an alias / complexity limit.");
        }

        // Data came back for the whole aliased batch → no alias / complexity cap.
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object)
        {
            return Finding("BWR-OWASP-API4-GRAPHQL-ALIAS-BATCHING", "GraphQL has no query-complexity / alias limit",
                $"A single request aliasing __typename {AliasBatchSize} times was resolved in full — the server applies no alias-count or query-complexity limit. One small request can fan out to arbitrary resolver work (alias-batching amplification), a direct denial-of-service lever unique to GraphQL's single endpoint.",
                "Enforce a query-complexity / cost limit and cap alias count + query depth before execution. Apollo Server: `@apollo/server` cost plugins or `graphql-query-complexity`; Hot Chocolate: `.AddMaxExecutionDepthRule(n)` + `.SetPagingOptions` + cost analysis; graphql-ruby: `max_complexity` / `max_depth`. Also apply a request-body size cap at the gateway.",
                "medium", 5.3);
        }

        return Marker(ScanFindingStatus.Skipped, "API4-GRAPHQL-INCONCLUSIVE", "GraphQL resource-limit probe inconclusive",
            "The amplification response carried neither a data object nor an errors array — the limit could not be determined.");
    }

    /// <summary>
    /// Classify a failed / non-2xx invocation by its status string: a status
    /// that names a rejection code (400 / 413 / 429) or a limit keyword means
    /// the server refused the oversized query (Safe); anything else is a
    /// transport wobble we can't read a verdict from (Skip).
    /// </summary>
    private ScanFinding ClassifyByStatus(string? status)
    {
        var s = status ?? "";
        if (s_rejectionSignals.Any(sig => s.Contains(sig, StringComparison.OrdinalIgnoreCase)))
        {
            return Marker(ScanFindingStatus.Safe, "API4-GRAPHQL-LIMIT-ENFORCED", "GraphQL query-complexity limit enforced",
                $"The server refused a query aliasing __typename {AliasBatchSize} times ({s}) — a request-size / complexity limit is enforced.");
        }

        return Marker(ScanFindingStatus.Skipped, "API4-GRAPHQL-INCONCLUSIVE", "GraphQL resource-limit probe inconclusive",
            $"The amplification query did not complete ({s}) — the limit could not be determined.");
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

    /// <summary>Build <c>{ a0: __typename a1: __typename … a{n-1}: __typename }</c>.</summary>
    private static string BuildAliasBatchQuery(int count)
    {
        var sb = new StringBuilder(count * 20);
        sb.Append("{ ");
        for (var i = 0; i < count; i++)
        {
            sb.Append('a').Append(i).Append(": __typename ");
        }
        sb.Append('}');
        return sb.ToString();
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
            remediation: "Diagnostic marker for the GraphQL resource-limit probe."),
        Status = status,
        Detail = detail,
    };
}
