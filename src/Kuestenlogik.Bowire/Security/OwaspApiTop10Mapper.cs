// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Security;

/// <summary>Tri-state per-entry risk for the OWASP panel (#106).</summary>
public enum OwaspRiskStatus
{
    /// <summary>No signal that this entry applies to the method.</summary>
    NotApplicable,
    /// <summary>A signal exists but needs a probe to confirm.</summary>
    Maybe,
    /// <summary>A concrete signal — actionable.</summary>
    AtRisk,
}

/// <summary>One OWASP API Top 10 row for a method.</summary>
/// <param name="Entry">e.g. <c>API1:2023</c>.</param>
/// <param name="Title">Short risk name.</param>
/// <param name="Status">Tri-state assessment.</param>
/// <param name="Rationale">Why the status was assigned.</param>
/// <param name="SuggestedProbe">The concrete probe to run (null for not-applicable rows).</param>
public sealed record OwaspPanelRow(string Entry, string Title, OwaspRiskStatus Status, string Rationale, string? SuggestedProbe);

/// <summary>The method shape the mapper reasons over.</summary>
/// <param name="Path">Request path / method route (e.g. <c>/orders/{id}</c>, <c>pkg.Svc/Delete</c>).</param>
/// <param name="Verb">HTTP verb / method kind (GET/POST/…); null when unknown.</param>
/// <param name="RequestFields">Request-body / input field names (for mass-assignment + SSRF signals).</param>
public sealed record OwaspMethodDescriptor(string Path, string? Verb = null, IReadOnlyList<string>? RequestFields = null);

/// <summary>
/// Deterministic OWASP API Security Top 10 (2023) mapping per method (#106):
/// each of the ten entries gets a tri-state status (at-risk / maybe / n-a) and a
/// concrete suggested probe, from rule-based signals on the method's path, verb,
/// and request fields. This is the ground truth the AI panel refines — and the
/// panel still works with no model connected.
/// </summary>
public static partial class OwaspApiTop10Mapper
{
    private static readonly (string Entry, string Title)[] s_entries =
    [
        ("API1:2023", "Broken Object Level Authorization"),
        ("API2:2023", "Broken Authentication"),
        ("API3:2023", "Broken Object Property Level Authorization"),
        ("API4:2023", "Unrestricted Resource Consumption"),
        ("API5:2023", "Broken Function Level Authorization"),
        ("API6:2023", "Unrestricted Access to Sensitive Business Flows"),
        ("API7:2023", "Server Side Request Forgery"),
        ("API8:2023", "Security Misconfiguration"),
        ("API9:2023", "Improper Inventory Management"),
        ("API10:2023", "Unsafe Consumption of APIs"),
    ];

    private static readonly string[] s_writeVerbs = ["POST", "PUT", "PATCH", "DELETE"];
    private static readonly string[] s_ssrfFieldHints = ["url", "uri", "callback", "webhook", "redirect", "target", "endpoint", "host", "link", "src", "dest"];

    /// <summary>Map a method against all ten OWASP API entries.</summary>
    public static IReadOnlyList<OwaspPanelRow> Map(OwaspMethodDescriptor method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var path = method.Path ?? "";
        var verb = (method.Verb ?? "").ToUpperInvariant();
        var fields = method.RequestFields ?? [];
        var isWrite = s_writeVerbs.Contains(verb) || (string.IsNullOrEmpty(verb) && fields.Count > 0);
        var hasSsrfField = fields.Any(f => s_ssrfFieldHints.Any(h => string.Equals(f, h, StringComparison.OrdinalIgnoreCase) || f.Contains(h, StringComparison.OrdinalIgnoreCase)));

        var rows = new List<OwaspPanelRow>(s_entries.Length);
        foreach (var (entry, title) in s_entries)
        {
            rows.Add(entry switch
            {
                "API1:2023" => IdInPath().IsMatch(path)
                    ? Row(entry, title, OwaspRiskStatus.AtRisk, "Object identifier in the path — object-level authorization must be enforced.", "BOLA probe: read the object as a second identity (--auth-header-b).")
                    : Na(entry, title, "No object identifier in the path."),
                "API2:2023" => AuthPath().IsMatch(path)
                    ? Row(entry, title, OwaspRiskStatus.Maybe, "Authentication surface — verify anonymous access is rejected.", "Anonymous-access + weak-credential probe.")
                    : Na(entry, title, "Not an authentication endpoint."),
                "API3:2023" => isWrite && fields.Count > 0
                    ? Row(entry, title, OwaspRiskStatus.AtRisk, "Write method accepting an object — mass-assignment / over-writable properties possible.", "Mass-assignment fuzz: inject unexpected privileged fields (e.g. role, isAdmin).")
                    : Na(entry, title, "No writable object body."),
                "API4:2023" => Row(entry, title, OwaspRiskStatus.Maybe, "Every method should enforce rate + body-size limits.", "Rate-limit burst + oversized-body probe."),
                "API5:2023" => AdminPath().IsMatch(path)
                    ? Row(entry, title, OwaspRiskStatus.AtRisk, "Administrative / privileged path — function-level authorization must gate it.", "Role-elevation probe: call the function without the privileged role.")
                    : Na(entry, title, "Not a privileged/administrative function."),
                "API6:2023" => Na(entry, title, "Business-flow abuse needs domain semantics — review manually."),
                "API7:2023" => hasSsrfField
                    ? Row(entry, title, OwaspRiskStatus.AtRisk, "A request field looks like a URL — the server may fetch attacker-controlled locations.", "SSRF probe: point the URL field at an internal / metadata address.")
                    : Na(entry, title, "No URL-shaped request field."),
                "API8:2023" => Row(entry, title, OwaspRiskStatus.Maybe, "Transport-level misconfig (CORS, security headers, verbose errors) is method-independent.", "CORS + security-headers + verbose-error check."),
                "API9:2023" => Na(entry, title, "Inventory exposure is a surface-level check, not per-method."),
                "API10:2023" => hasSsrfField
                    ? Row(entry, title, OwaspRiskStatus.Maybe, "The method may consume an upstream URL — validate response handling of untrusted upstreams.", "Unsafe-consumption review of the upstream call.")
                    : Na(entry, title, "No outbound/upstream consumption signal."),
                _ => Na(entry, title, "n/a"),
            });
        }
        return rows;
    }

    private static OwaspPanelRow Row(string e, string t, OwaspRiskStatus s, string why, string probe) => new(e, t, s, why, probe);
    private static OwaspPanelRow Na(string e, string t, string why) => new(e, t, OwaspRiskStatus.NotApplicable, why, null);

    [GeneratedRegex(@"\{[^}]+\}|/:\w+|/\d+(/|$)")]
    private static partial Regex IdInPath();
    [GeneratedRegex(@"/(admin|internal|debug|sudo|root|management|actuator)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AdminPath();
    [GeneratedRegex(@"/(auth|login|logout|token|oauth|sso|saml|register|signin|signup|password)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AuthPath();
}
