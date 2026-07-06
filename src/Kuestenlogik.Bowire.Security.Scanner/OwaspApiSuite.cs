// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>Per-entry status after rolling a scan's findings up against the OWASP catalog.</summary>
internal enum OwaspEntryStatus
{
    /// <summary>At least one finding tagged to this entry came back Vulnerable.</summary>
    Vulnerable,
    /// <summary>Findings ran for this entry and none were Vulnerable.</summary>
    Safe,
    /// <summary>No finding in this scan maps to this entry — no probe exercised it yet.</summary>
    NotCovered,
    /// <summary>Every finding that mapped to this entry errored (probe couldn't complete).</summary>
    Error,
}

/// <summary>Roll-up of one OWASP Top-10 entry for a single scan.</summary>
internal sealed record OwaspEntryResult(OwaspApiEntry Entry, OwaspEntryStatus Status, int VulnCount, int ProbeCount);

/// <summary>
/// A default probe for one OWASP API Top 10 (2023) entry. Runs when
/// <c>--suite=owasp-api</c> is active and emits findings tagged with the
/// entry's <c>APIn-2023-</c> id so they roll up into that entry's row. A
/// probe that finds nothing should still emit one Safe finding so the entry
/// reads as "covered, clean" rather than "no probe yet".
/// </summary>
internal interface IOwaspApiProbe
{
    /// <summary>The OWASP Top-10 entry this probe covers.</summary>
    OwaspApiEntry Entry { get; }

    /// <summary>
    /// Run the probe against the target and return its findings.
    /// <paramref name="authHeadersB"/> carries an optional *second* identity's
    /// headers (from <c>--auth-header-b</c>) — used by cross-identity checks
    /// like BOLA; probes that don't need it ignore it.
    /// </summary>
    Task<IReadOnlyList<ScanFinding>> RunAsync(string target, HttpClient http, IList<string> authHeaders, IList<string> authHeadersB, CancellationToken ct);
}

/// <summary>
/// The <c>--suite=owasp-api</c> view. It does not run its own probes —
/// instead it rolls the scan's existing findings (templates + built-ins,
/// each carrying an <c>OwaspApi</c> tag) up against
/// <see cref="OwaspApiCatalog"/> so the operator sees, per OWASP API Top 10
/// (2023) entry, whether this scan exercised it and whether it was clean.
/// Entries with no mapped finding surface as <see cref="OwaspEntryStatus.NotCovered"/>
/// — an honest "no probe yet" rather than a false pass. Dedicated per-entry
/// probes (BOLA, Broken-Auth, SSRF, …) land incrementally and light up their
/// row automatically as their findings gain the matching tag.
/// </summary>
internal static class OwaspApiSuite
{
    /// <summary>
    /// The dedicated default probes, one (eventually) per Top-10 entry.
    /// Grows as each entry's probe lands; entries with no probe yet roll up
    /// from whatever templates / built-ins happen to be tagged for them.
    /// </summary>
    public static IReadOnlyList<IOwaspApiProbe> Probes { get; } =
    [
        new Api1BolaProbe(),
        new Api2AuthProbe(),
        new Api4ResourceProbe(),
        new Api5AuthorizationProbe(),
        new Api7SsrfProbe(),
        new Api8ConfigProbe(),
        new Api9InventoryProbe(),
    ];

    /// <summary>
    /// Run every registered OWASP probe against the target and return the
    /// merged findings. A probe that throws is isolated into a single Error
    /// finding for its entry so one wedged probe can't sink the suite.
    /// </summary>
    public static async Task<IReadOnlyList<ScanFinding>> RunProbesAsync(string target, HttpClient http, IList<string> authHeaders, IList<string> authHeadersB, CancellationToken ct)
    {
        var merged = new List<ScanFinding>();
        foreach (var probe in Probes)
        {
            try
            {
                merged.AddRange(await probe.RunAsync(target, http, authHeaders, authHeadersB, ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                merged.Add(new ScanFinding
                {
                    Template = SyntheticTemplate.Build(
                        id: $"BWR-OWASP-{probe.Entry.Id.Replace(':', '-')}-ERROR",
                        name: $"{probe.Entry.Title} probe errored",
                        cwe: null, owaspApi: probe.Entry.Tag, severity: "info", cvss: null,
                        remediation: "Diagnostic — the probe could not complete; re-run with --verbose or check target reachability."),
                    Status = ScanFindingStatus.Error,
                    Detail = ex.Message,
                });
            }
        }
        return merged;
    }

    /// <summary>Group the findings by OWASP entry and derive a per-entry status for all ten.</summary>
    public static IReadOnlyList<OwaspEntryResult> Rollup(IEnumerable<ScanFinding> findings)
    {
        var byEntry = new Dictionary<string, List<ScanFinding>>(StringComparer.Ordinal);
        foreach (var finding in findings)
        {
            var entry = OwaspApiCatalog.Match(finding.Template.Recording.Vulnerability?.OwaspApi);
            if (entry is null) continue;
            if (!byEntry.TryGetValue(entry.Id, out var bucket))
            {
                bucket = [];
                byEntry[entry.Id] = bucket;
            }
            bucket.Add(finding);
        }

        var results = new List<OwaspEntryResult>(OwaspApiCatalog.Entries.Count);
        foreach (var entry in OwaspApiCatalog.Entries)
        {
            if (!byEntry.TryGetValue(entry.Id, out var bucket) || bucket.Count == 0)
            {
                results.Add(new OwaspEntryResult(entry, OwaspEntryStatus.NotCovered, 0, 0));
                continue;
            }

            // Only an actual Safe or Vulnerable verdict counts as "assessed".
            // A bucket that is entirely Skipped (the probe needed input it
            // didn't have — no credential, no URL parameter) must NOT read as a
            // clean pass: report NotCovered so the coverage table stays honest.
            var vuln = bucket.Count(f => f.Status == ScanFindingStatus.Vulnerable);
            var safe = bucket.Count(f => f.Status == ScanFindingStatus.Safe);
            OwaspEntryStatus status;
            if (vuln > 0) status = OwaspEntryStatus.Vulnerable;
            else if (safe > 0) status = OwaspEntryStatus.Safe;
            else if (bucket.All(f => f.Status == ScanFindingStatus.Error)) status = OwaspEntryStatus.Error;
            else status = OwaspEntryStatus.NotCovered;
            results.Add(new OwaspEntryResult(entry, status, vuln, bucket.Count));
        }
        return results;
    }

    /// <summary>Write the per-entry OWASP coverage table to the report stream.</summary>
    public static async Task WriteSummaryAsync(IEnumerable<ScanFinding> findings, TextWriter stdout)
    {
        var rollup = Rollup(findings);
        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync("  OWASP API Security Top 10 (2023) — suite coverage:").ConfigureAwait(false);
        foreach (var result in rollup)
        {
            var marker = result.Status switch
            {
                OwaspEntryStatus.Vulnerable => "[VULN]",
                OwaspEntryStatus.Safe => "[ok]  ",
                OwaspEntryStatus.Error => "[err] ",
                _ => "[----]",
            };
            var note = result.Status switch
            {
                OwaspEntryStatus.Vulnerable => $"{result.VulnCount} finding(s) across {result.ProbeCount} probe(s)",
                OwaspEntryStatus.Safe => $"{result.ProbeCount} probe(s), clean",
                OwaspEntryStatus.Error => "probe error",
                _ => "no probe yet — not exercised by this scan",
            };
            await stdout.WriteLineAsync($"  {marker} {result.Entry.Id,-10} {result.Entry.Title,-48} {note}").ConfigureAwait(false);
        }

        var covered = rollup.Count(r => r.Status != OwaspEntryStatus.NotCovered);
        var vulnerable = rollup.Count(r => r.Status == OwaspEntryStatus.Vulnerable);
        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync($"  {covered}/10 Top-10 entries exercised; {vulnerable} with vulnerability finding(s).").ConfigureAwait(false);
        if (covered < OwaspApiCatalog.Entries.Count)
        {
            await stdout.WriteLineAsync("  '[----]' entries have no probe yet — a clean scan is NOT a pass for them.").ConfigureAwait(false);
        }
    }
}
