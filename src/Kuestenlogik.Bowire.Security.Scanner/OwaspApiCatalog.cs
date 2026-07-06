// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// One entry in the OWASP API Security Top 10 (2023) taxonomy.
/// </summary>
/// <param name="Id">Canonical id, e.g. <c>API8:2023</c>.</param>
/// <param name="Tag">Representative finding tag for this entry, e.g.
/// <c>API8-2023-SECMISCONF</c> — the shape carried by
/// <see cref="Kuestenlogik.Bowire.Security.AttackVulnerability.OwaspApi"/>.
/// Roll-up matches on the numeric <c>APIn-2023-</c> prefix, so any tag
/// sharing that prefix maps to this entry regardless of its short suffix.</param>
/// <param name="Title">Human-readable entry title.</param>
/// <param name="Reference">Canonical OWASP reference URL.</param>
public sealed record OwaspApiEntry(string Id, string Tag, string Title, string Reference)
{
    /// <summary>
    /// The <c>APIn-2023-</c> prefix a finding tag must start with to roll
    /// up to this entry. The trailing dash disambiguates <c>API1</c> from
    /// <c>API10</c> (<c>API10-2023-…</c> does not start with <c>API1-2023-</c>).
    /// </summary>
    public string Prefix { get; } = Id.Replace(':', '-') + "-";
}

/// <summary>
/// Canonical OWASP API Security Top 10 (2023) taxonomy — the structured
/// backbone of the <c>--suite=owasp-api</c> scan mode. Scan findings tag
/// themselves with an <c>APIn-2023-…</c> OWASP id (see
/// <see cref="Kuestenlogik.Bowire.Security.AttackVulnerability.OwaspApi"/>);
/// this catalog rolls those tags up into the ten Top-10 entries so a scan
/// can report per-entry covered / clean / vulnerable status. Public so the
/// workbench Security rail + API can enumerate the ten entries independently
/// of any single scan run.
/// </summary>
public static class OwaspApiCatalog
{
    /// <summary>The ten OWASP API Security Top 10 (2023) entries, in order.</summary>
    public static IReadOnlyList<OwaspApiEntry> Entries { get; } =
    [
        new("API1:2023", "API1-2023-BOLA", "Broken Object Level Authorization",
            "https://owasp.org/API-Security/editions/2023/en/0xa1-broken-object-level-authorization/"),
        new("API2:2023", "API2-2023-BROKENAUTH", "Broken Authentication",
            "https://owasp.org/API-Security/editions/2023/en/0xa2-broken-authentication/"),
        new("API3:2023", "API3-2023-BOPLA", "Broken Object Property Level Authorization",
            "https://owasp.org/API-Security/editions/2023/en/0xa3-broken-object-property-level-authorization/"),
        new("API4:2023", "API4-2023-RESOURCE", "Unrestricted Resource Consumption",
            "https://owasp.org/API-Security/editions/2023/en/0xa4-unrestricted-resource-consumption/"),
        new("API5:2023", "API5-2023-BFLA", "Broken Function Level Authorization",
            "https://owasp.org/API-Security/editions/2023/en/0xa5-broken-function-level-authorization/"),
        new("API6:2023", "API6-2023-BUSFLOW", "Unrestricted Access to Sensitive Business Flows",
            "https://owasp.org/API-Security/editions/2023/en/0xa6-unrestricted-access-to-sensitive-business-flows/"),
        new("API7:2023", "API7-2023-SSRF", "Server Side Request Forgery",
            "https://owasp.org/API-Security/editions/2023/en/0xa7-server-side-request-forgery/"),
        new("API8:2023", "API8-2023-SECMISCONF", "Security Misconfiguration",
            "https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/"),
        new("API9:2023", "API9-2023-INVENTORY", "Improper Inventory Management",
            "https://owasp.org/API-Security/editions/2023/en/0xa9-improper-inventory-management/"),
        new("API10:2023", "API10-2023-UNSAFE", "Unsafe Consumption of APIs",
            "https://owasp.org/API-Security/editions/2023/en/0xaa-unsafe-consumption-of-apis/"),
    ];

    /// <summary>
    /// Map a finding's <c>OwaspApi</c> tag (e.g. <c>API8-2023-SECMISCONF</c>)
    /// to its Top-10 entry, or <c>null</c> when the tag is absent or doesn't
    /// follow the <c>APIn-2023-</c> convention.
    /// </summary>
    public static OwaspApiEntry? Match(string? owaspApiTag)
    {
        if (string.IsNullOrEmpty(owaspApiTag)) return null;
        foreach (var entry in Entries)
        {
            if (owaspApiTag.StartsWith(entry.Prefix, StringComparison.OrdinalIgnoreCase))
                return entry;
        }
        return null;
    }
}
