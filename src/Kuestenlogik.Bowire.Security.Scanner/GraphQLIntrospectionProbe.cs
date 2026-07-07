// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Protocol probe for GraphQL, rolling up to <c>API9:2023 — Improper Inventory
/// Management</c>. It drives the GraphQL plugin's discovery path, which runs
/// the standard <c>__schema</c> introspection query <em>anonymously</em>. A
/// schema coming back means introspection is publicly readable — the GraphQL
/// analog of an exposed OpenAPI / Swagger document (the HTTP
/// <see cref="Api9InventoryProbe"/> flags the REST version): any client can
/// map the entire API surface — every type, field, argument, and enum.
///
/// <para>Discovery-only, so it never mutates the target. When the endpoint
/// answers no schema (not GraphQL, or introspection disabled — the desired
/// production state) the probe skips rather than reporting a false pass.</para>
/// </summary>
internal sealed class GraphQLIntrospectionProbe : IOwaspProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API9:2023");

    public string ProtocolId => "graphql";

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct)
    {
        List<BowireServiceInfo> types;
        try
        {
            // DiscoverAsync POSTs the __schema introspection query with no
            // credentials — exactly the anonymous exposure we want to detect.
            types = await protocol.DiscoverAsync(target, showInternalServices: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker(ScanFindingStatus.Skipped, "API9-GRAPHQL-UNREACHABLE", "GraphQL introspection probe skipped",
                $"Anonymous GraphQL introspection could not be attempted ({ex.GetType().Name}).")];
        }

        var rootCount = types.Count;
        var operationCount = types.Sum(t => t.Methods.Count);
        if (rootCount == 0)
        {
            return [Marker(ScanFindingStatus.Skipped, "API9-GRAPHQL-NO-INTROSPECTION", "GraphQL introspection not exposed",
                "An anonymous introspection query returned no schema — the target is not a GraphQL endpoint, or introspection is disabled (the desired production state).")];
        }

        return [Finding("BWR-OWASP-API9-GRAPHQL-INTROSPECTION", "GraphQL introspection enabled",
            $"An anonymous introspection query (__schema) returned the schema — {operationCount} operation(s) across {rootCount} root type(s). Public introspection lets any client map the entire API surface (every type, field, argument, enum), the GraphQL analog of an exposed OpenAPI document.",
            "Disable introspection in production, or gate it behind auth — Apollo Server: `introspection: false`; Hot Chocolate: `ModifyOptions(o => o.EnableSchemaRequests = false)`; GraphQL.NET: a schema/validation rule that rejects `__schema` / `__type`. Keep it on only in dev.",
            "medium", 5.3)];
    }

    // ---- finding factories ----

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-200", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the GraphQL introspection probe."),
        Status = status,
        Detail = detail,
    };
}
