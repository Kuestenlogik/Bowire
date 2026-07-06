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

            var vuln = bucket.Count(f => f.Status == ScanFindingStatus.Vulnerable);
            var status = vuln > 0
                ? OwaspEntryStatus.Vulnerable
                : bucket.All(f => f.Status == ScanFindingStatus.Error)
                    ? OwaspEntryStatus.Error
                    : OwaspEntryStatus.Safe;
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
