// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>Collects what an import or export did with each part of the source.</summary>
internal sealed class ReportBuilder
{
    // The header names the HAR importer treats as credentials, plus the two
    // custom-auth spellings people use in collections.
    private static readonly HashSet<string> s_credentialHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "X-Api-Key", "X-Api-Token", "X-Auth-Token",
        "X-Access-Token", "X-Csrf-Token", "X-Xsrf-Token", "Api-Key", "Authentication",
    };

    private readonly ImportReport _report = new();

    public void Mapped(string subject, string detail = "") => Add(ReportEntry.Types.Kind.Mapped, subject, detail);

    public void Approximated(string subject, string detail) => Add(ReportEntry.Types.Kind.Approximated, subject, detail);

    public void Skipped(string subject, string detail) => Add(ReportEntry.Types.Kind.Skipped, subject, detail);

    public void Credential(string subject, string field) =>
        _report.Credentials.Add(new CredentialFinding { Subject = subject, Field = field });

    /// <summary>
    /// Flag every header whose name says credential and whose value is a
    /// literal rather than a <c>{{variable}}</c> reference.
    /// </summary>
    public void CheckHeaders(string subject, IEnumerable<KeyValuePair<string, string>> headers)
    {
        foreach (var (key, value) in headers)
        {
            if (s_credentialHeaders.Contains(key) && !IsReference(value)) Credential(subject, key + " header");
        }
    }

    /// <summary>Whether <paramref name="value"/> is only a variable reference, which carries no secret.</summary>
    public static bool IsReference(string value)
    {
        var trimmed = value.Trim();
        // "Bearer {{token}}" carries no secret either: everything but the scheme is a reference.
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0 && trimmed.IndexOf("{{", StringComparison.Ordinal) == space + 1) trimmed = trimmed[(space + 1)..];
        return trimmed.Length == 0
            || (trimmed.StartsWith("{{", StringComparison.Ordinal) && trimmed.EndsWith("}}", StringComparison.Ordinal)
                && trimmed.IndexOf("}}", StringComparison.Ordinal) == trimmed.Length - 2);
    }

    public ImportReport Build() => _report;

    private void Add(ReportEntry.Types.Kind kind, string subject, string detail) =>
        _report.Entries.Add(new ReportEntry { Kind = kind, Subject = subject, Detail = detail });
}
