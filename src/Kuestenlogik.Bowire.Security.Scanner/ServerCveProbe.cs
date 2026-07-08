// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Passive CVE-lookup probe (#187): reads the target's <c>Server</c> /
/// <c>X-Powered-By</c> banners, parses the product + version, and matches them
/// against a <see cref="CveDatabase"/> (the <c>Bowire.VulnDb</c> corpus, or the
/// built-in seed). Each match is a finding tagged with the CVE — the scanner's
/// answer to OWASP A06 "Vulnerable and Outdated Components". Purely passive: one
/// GET, no exploitation.
/// </summary>
internal static class ServerCveProbe
{
    // Banner headers that carry a product/version worth matching.
    private static readonly string[] s_bannerHeaders = ["Server", "X-Powered-By"];

    public static async Task<IReadOnlyList<ScanFinding>> RunAsync(
        string target, HttpClient http, IList<string> authHeaders, CveDatabase db, CancellationToken ct)
    {
        var findings = new List<ScanFinding>();
        string? bannerSeen = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, target);
            ScanCommand.ApplyAuthHeaders(req, authHeaders);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

            foreach (var header in s_bannerHeaders)
            {
                if (!resp.Headers.TryGetValues(header, out var values)) continue;
                foreach (var raw in values)
                {
                    var (product, version) = ParseBanner(raw);
                    if (product is null || version is null) continue;
                    bannerSeen = raw;

                    foreach (var cve in db.Match(product, version))
                        findings.Add(Vulnerable(product, version, cve));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [new ScanFinding
            {
                Template = SyntheticTemplate.Build("BWR-CVE-ERROR", "CVE lookup could not run",
                    cwe: null, owaspApi: null, severity: "info", cvss: null,
                    remediation: "Diagnostic — the banner request failed; CVE lookup skipped."),
                Status = ScanFindingStatus.Error,
                Detail = ex.Message,
            }];
        }

        // Banner present but no known CVE → report the check ran clean, so the
        // operator can tell "no match" apart from "probe didn't run".
        if (findings.Count == 0 && bannerSeen is not null)
        {
            findings.Add(new ScanFinding
            {
                Template = SyntheticTemplate.Build("BWR-CVE-CLEAN", "No known CVEs for the detected server banner",
                    cwe: null, owaspApi: null, severity: "info", cvss: null,
                    remediation: $"Diagnostic — no entry in the CVE database ({db.Count} record(s)) matched the banner."),
                Status = ScanFindingStatus.Safe,
                Detail = $"Banner '{bannerSeen}' matched no CVE in the database.",
            });
        }

        return findings;
    }

    private static ScanFinding Vulnerable(string product, string version, CveEntry cve) => new()
    {
        Template = SyntheticTemplate.Build(
            id: "BWR-CVE-" + cve.Cve,
            name: $"{product} {version} — {cve.Cve}",
            cwe: "CWE-1395", // Dependency on a vulnerable third-party component
            owaspApi: "API8-2023-SECMISCONF",
            severity: cve.Severity,
            cvss: cve.Cvss,
            remediation: BuildRemediation(product, cve)),
        Status = ScanFindingStatus.Vulnerable,
        Detail = $"{product} {version} is affected by {cve.Cve}"
            + (cve.Title is null ? "" : $" — {cve.Title}")
            + (cve.Fixed is null ? "." : $"; fixed in {cve.Fixed}."),
    };

    private static string BuildRemediation(string product, CveEntry cve)
    {
        var upgrade = cve.Fixed is null
            ? $"Upgrade {product} to a patched release."
            : $"Upgrade {product} to {cve.Fixed} or later.";
        return cve.Reference is null ? upgrade : upgrade + " See " + cve.Reference;
    }

    /// <summary>
    /// Parse a banner like <c>nginx/1.18.0</c>, <c>Apache/2.4.49 (Ubuntu)</c>,
    /// <c>Microsoft-IIS/10.0</c>, or <c>PHP/7.4.3</c> into (product, version).
    /// Returns (null, null) when there's no <c>product/version</c> shape.
    /// </summary>
    internal static (string? Product, string? Version) ParseBanner(string banner)
    {
        if (string.IsNullOrWhiteSpace(banner)) return (null, null);
        var slash = banner.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == banner.Length - 1) return (null, null);

        var product = banner[..slash].Trim();
        var rest = banner[(slash + 1)..];
        // Version is the leading token up to a space or '('.
        var end = 0;
        while (end < rest.Length && rest[end] != ' ' && rest[end] != '(') end++;
        var version = rest[..end].Trim();

        if (product.Length == 0 || version.Length == 0 || !char.IsDigit(version[0]))
            return (null, null);
        return (product, version);
    }
}
