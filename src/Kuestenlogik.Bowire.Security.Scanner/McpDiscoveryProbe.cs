// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Protocol probe for MCP (Model Context Protocol), rolling up to
/// <c>API9:2023 — Improper Inventory Management</c>. It drives the MCP plugin's
/// discovery path, which performs the MCP <c>initialize</c> handshake and lists
/// the server's tools, resources, and prompts <em>anonymously</em> (the plugin
/// sends no credentials on discovery). A populated listing means the whole tool
/// / resource surface is enumerable without authentication — the MCP analog of
/// an exposed API inventory (like gRPC reflection or GraphQL introspection),
/// and a direct lead-in to tool-call abuse.
///
/// <para>Discovery-only: it lists but never invokes a tool, so it can't trigger
/// a tool's side effects. A non-MCP endpoint or one that gates the handshake
/// returns nothing and the probe skips rather than reporting a false pass.</para>
/// </summary>
internal sealed class McpDiscoveryProbe : IOwaspProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API9:2023");

    public string ProtocolId => "mcp";

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct)
    {
        List<BowireServiceInfo> surfaces;
        try
        {
            // DiscoverAsync runs the MCP initialize handshake + list operations
            // with no credentials — exactly the anonymous exposure to detect.
            surfaces = await protocol.DiscoverAsync(target, showInternalServices: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker(ScanFindingStatus.Skipped, "API9-MCP-UNREACHABLE", "MCP discovery probe skipped",
                $"Anonymous MCP discovery could not be attempted ({ex.GetType().Name}).")];
        }

        var itemCount = surfaces.Sum(s => s.Methods.Count);
        if (surfaces.Count == 0 || itemCount == 0)
        {
            return [Marker(ScanFindingStatus.Skipped, "API9-MCP-NO-LISTING", "MCP inventory not exposed",
                "An anonymous MCP handshake returned no tools / resources / prompts — the target is not an MCP server, or it gates discovery behind authentication (the desired production state).")];
        }

        var groups = string.Join(", ", surfaces.Where(s => s.Methods.Count > 0).Select(s => s.Name + " (" + s.Methods.Count + ")"));
        return [Finding("BWR-OWASP-API9-MCP-DISCOVERY", "MCP tools / resources enumerable anonymously",
            $"An anonymous MCP handshake enumerated {itemCount} item(s) across {surfaces.Count(s => s.Methods.Count > 0)} surface(s) ({groups}). A publicly listable MCP server hands any client the full catalogue of tools, resources, and prompts — the inventory an attacker needs to plan tool-call abuse or resource traversal.",
            "Require authentication on the MCP endpoint before the initialize handshake (an auth-provider in front of the transport), or expose only a vetted, least-privilege tool set. Never ship an unauthenticated MCP server that lists privileged tools.",
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
            remediation: "Diagnostic marker for the MCP discovery probe."),
        Status = status,
        Detail = detail,
    };
}
