// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Security;

/// <summary>
/// One finding lifted out of a SARIF run for the report. <c>Location</c>
/// is where the finding is: the scan target for a DAST result (its SARIF
/// <c>logicalLocation</c>), otherwise the file the SARIF points at.
/// <c>Remediation</c> is what to do about it — the rule's full
/// description — and <c>HelpUri</c> the rule's reference; both null when
/// the SARIF carries none.
/// </summary>
public sealed record SecurityReportFinding(
    string RuleId, string Title, string Severity, string? OwaspApi, double? Cvss, string Location, string Fingerprint,
    string? Remediation = null, string? HelpUri = null);

/// <summary>Diff of a scan against a baseline run.</summary>
public sealed record SecurityReportDiff(
    IReadOnlyList<SecurityReportFinding> New,
    IReadOnlyList<SecurityReportFinding> Fixed,
    IReadOnlyList<SecurityReportFinding> Persisting);

/// <summary>
/// Structured security report (#107): findings grouped by severity + OWASP,
/// plus a diff against a baseline run. <see cref="ToMarkdown"/> renders the
/// deterministic markdown skeleton; an AI layer adds the executive-summary prose.
/// </summary>
public sealed record SecurityReport(
    string? Target,
    IReadOnlyList<SecurityReportFinding> Findings,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByOwasp,
    SecurityReportDiff? Diff)
{
    private static readonly string[] s_severityOrder = ["critical", "high", "medium", "low", "info"];

    /// <summary>Deterministic markdown skeleton — no AI. An executive summary can be prepended.</summary>
    public string ToMarkdown(string? executiveSummary = null)
    {
        var sb = new StringBuilder();
        sb.Append("# Security scan report");
        if (!string.IsNullOrWhiteSpace(Target)) sb.Append(" — ").Append(Target);
        sb.Append("\n\n");

        var totals = string.Join(", ", s_severityOrder
            .Where(s => BySeverity.TryGetValue(s, out var n) && n > 0)
            .Select(s => $"{BySeverity[s]} {s}"));
        sb.Append("**Findings:** ").Append(Findings.Count == 0 ? "none" : totals).Append("\n\n");

        if (Diff is not null)
            sb.Append(CultureInfo.InvariantCulture, $"**Since baseline:** {Diff.New.Count} new, {Diff.Fixed.Count} fixed, {Diff.Persisting.Count} still open.\n\n");

        if (!string.IsNullOrWhiteSpace(executiveSummary))
            sb.Append("## Executive summary\n\n").Append(executiveSummary.Trim()).Append("\n\n");

        if (ByOwasp.Count > 0)
        {
            sb.Append("## By OWASP API Top 10\n\n| Entry | Risk | Findings |\n| --- | --- | --- |\n");
            foreach (var (owasp, n) in ByOwasp.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append("| ").Append(OwaspLink(owasp)).Append(" | ").Append(OwaspName(owasp)).Append(" | ").Append(n).Append(" |\n");
            sb.Append('\n');
        }

        sb.Append("## Findings\n\n");
        if (Findings.Count == 0)
        {
            sb.Append("No findings.\n\n");
        }
        else
        {
            foreach (var f in Findings.OrderBy(f => SeverityRank(f.Severity)).ThenBy(f => f.RuleId, StringComparer.Ordinal))
            {
                sb.Append("### [").Append(f.Severity).Append("] ").Append(f.Title).Append('\n');
                var meta = new List<string> { $"**Rule**: `{f.RuleId}`" };
                if (!string.IsNullOrEmpty(f.OwaspApi)) meta.Add($"**OWASP**: {OwaspLink(f.OwaspApi)} — {OwaspName(f.OwaspApi)}");
                if (f.Cvss is { } c) meta.Add($"**CVSS**: {c.ToString("F1", CultureInfo.InvariantCulture)}");
                if (!string.IsNullOrEmpty(f.Location)) meta.Add($"**{LocationLabel(f.Location)}**: {LocationLink(f.Location)}");
                sb.Append(string.Join(" · ", meta)).Append('\n');
                if (!string.IsNullOrWhiteSpace(f.Remediation))
                    sb.Append("**What to do**: ").Append(f.Remediation.Trim()).Append('\n');
                if (!string.IsNullOrWhiteSpace(f.HelpUri))
                    sb.Append("**Reference**: <").Append(f.HelpUri.Trim()).Append(">\n");
                sb.Append('\n');
            }
        }

        if (Diff is not null && (Diff.New.Count > 0 || Diff.Fixed.Count > 0))
        {
            sb.Append("## Diff vs baseline\n\n");
            AppendDiffList(sb, "🆕 New", Diff.New);
            AppendDiffList(sb, "✅ Fixed", Diff.Fixed);
        }

        return sb.ToString();
    }

    private static void AppendDiffList(StringBuilder sb, string heading, IReadOnlyList<SecurityReportFinding> items)
    {
        if (items.Count == 0) return;
        sb.Append("**").Append(heading).Append("**\n\n");
        foreach (var f in items.OrderBy(f => f.RuleId, StringComparer.Ordinal))
            sb.Append("- [").Append(f.Severity).Append("] ").Append(f.Title).Append(" (`").Append(f.RuleId).Append("`)\n");
        sb.Append('\n');
    }

    internal static int SeverityRank(string s) => Array.IndexOf(s_severityOrder, s) is var i && i >= 0 ? i : s_severityOrder.Length;

    /// <summary>
    /// The OWASP API Security Top 10 (2023) by the code the scanner
    /// stamps on a rule — <c>API8-2023-SECMISCONF</c> is a label, not an
    /// explanation, and a report that names the risk saves the reader
    /// the lookup.
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Slug)> s_owasp = new(StringComparer.Ordinal)
    {
        ["API1"] = ("Broken Object Level Authorization", "broken-object-level-authorization"),
        ["API2"] = ("Broken Authentication", "broken-authentication"),
        ["API3"] = ("Broken Object Property Level Authorization", "broken-object-property-level-authorization"),
        ["API4"] = ("Unrestricted Resource Consumption", "unrestricted-resource-consumption"),
        ["API5"] = ("Broken Function Level Authorization", "broken-function-level-authorization"),
        ["API6"] = ("Unrestricted Access to Sensitive Business Flows", "unrestricted-access-to-sensitive-business-flows"),
        ["API7"] = ("Server Side Request Forgery", "server-side-request-forgery"),
        ["API8"] = ("Security Misconfiguration", "security-misconfiguration"),
        ["API9"] = ("Improper Inventory Management", "improper-inventory-management"),
        ["API10"] = ("Unsafe Consumption of APIs", "unsafe-consumption-of-apis"),
    };

    private static (string Name, string Slug)? OwaspEntry(string code)
    {
        var dash = code.IndexOf('-', StringComparison.Ordinal);
        var key = dash > 0 ? code[..dash] : code;
        return s_owasp.TryGetValue(key.ToUpperInvariant(), out var e) ? e : null;
    }

    /// <summary>The risk's name for a scanner OWASP code, or the code itself when it is not a Top-10 entry.</summary>
    public static string OwaspName(string code) => OwaspEntry(code)?.Name ?? code;

    /// <summary>The code, linked to its page on owasp.org when it is a Top-10 entry.</summary>
    public static string OwaspLink(string code)
        => OwaspEntry(code) is { } e ? $"[{code}](https://owasp.org/API-Security/editions/2023/en/0x{OwaspOrdinal(code)}-{e.Slug}/)" : code;

    // API1 → "a1"... the site numbers its pages 0xa1..0xaa.
    private static string OwaspOrdinal(string code)
    {
        var dash = code.IndexOf('-', StringComparison.Ordinal);
        var key = (dash > 0 ? code[..dash] : code).ToUpperInvariant();
        return key switch
        {
            "API10" => "aa",
            _ when key.Length == 4 && char.IsDigit(key[3]) => "a" + key[3],
            _ => "a0",
        };
    }

    internal static bool IsUrl(string location)
        => location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || location.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || location.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
        || location.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
        || location.StartsWith("grpc://", StringComparison.OrdinalIgnoreCase)
        || location.StartsWith("grpcs://", StringComparison.OrdinalIgnoreCase);

    private static string LocationLabel(string location) => IsUrl(location) ? "Target" : "Location";

    /// <summary>
    /// A location the reader can open: a URL as an autolink, a
    /// checkout-relative file as a link into the repository at the
    /// commit under test when the GitHub Actions environment says which
    /// one that is, and plain text otherwise.
    /// </summary>
    private static string LocationLink(string location)
    {
        if (IsUrl(location)) return "<" + location + ">";
        if (!location.Contains('/', StringComparison.Ordinal) && !location.Contains('.', StringComparison.Ordinal)) return location;
        var server = Environment.GetEnvironmentVariable("GITHUB_SERVER_URL");
        var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var sha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(sha)) return location;
        if (string.IsNullOrWhiteSpace(server)) server = "https://github.com";
        var path = location.Replace('\\', '/').TrimStart('/');
        return $"[{location}]({server.TrimEnd('/')}/{repo}/blob/{sha}/{path})";
    }
}

/// <summary>
/// Builds a <see cref="SecurityReport"/> from a scanner SARIF document (the
/// <c>bowire scan --out</c> artifact), optionally diffed against a baseline
/// SARIF. Deterministic — no AI, no network.
/// </summary>
public static class SecurityReportBuilder
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parse <paramref name="sarifJson"/> into a report, optionally diffed against <paramref name="baselineSarifJson"/>.</summary>
    public static SecurityReport Build(string sarifJson, string? baselineSarifJson = null, string? target = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sarifJson);
        var findings = ParseFindings(sarifJson);

        var bySeverity = findings.GroupBy(f => f.Severity, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byOwasp = findings.Where(f => !string.IsNullOrEmpty(f.OwaspApi))
            .GroupBy(f => f.OwaspApi!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        SecurityReportDiff? diff = null;
        if (!string.IsNullOrWhiteSpace(baselineSarifJson))
        {
            var baseline = ParseFindings(baselineSarifJson);
            var currentKeys = findings.Select(Key).ToHashSet(StringComparer.Ordinal);
            var baselineKeys = baseline.Select(Key).ToHashSet(StringComparer.Ordinal);
            diff = new SecurityReportDiff(
                New: findings.Where(f => !baselineKeys.Contains(Key(f))).ToArray(),
                Fixed: baseline.Where(f => !currentKeys.Contains(Key(f))).ToArray(),
                Persisting: findings.Where(f => baselineKeys.Contains(Key(f))).ToArray());
        }

        // The scan's own target, when the caller did not say: a DAST run
        // names it on every result, and a report headed by the URL it
        // scanned reads better than one headed by nothing.
        if (string.IsNullOrWhiteSpace(target))
        {
            var targets = findings.Select(f => f.Location).Where(SecurityReport.IsUrl).Distinct(StringComparer.Ordinal).ToList();
            if (targets.Count == 1) target = targets[0];
        }

        return new SecurityReport(target, findings, bySeverity, byOwasp, diff);
    }

    // Stable cross-run identity: the finding's fingerprint if present, else ruleId+location.
    private static string Key(SecurityReportFinding f)
        => !string.IsNullOrEmpty(f.Fingerprint) ? f.Fingerprint : f.RuleId + "|" + f.Location;

    private static List<SecurityReportFinding> ParseFindings(string sarifJson)
    {
        var doc = JsonSerializer.Deserialize<SarifDoc>(sarifJson, s_json)
            ?? throw new FormatException("SARIF document parsed to null.");
        var findings = new List<SecurityReportFinding>();
        foreach (var run in doc.Runs ?? [])
        {
            var rules = (run.Tool?.Driver?.Rules ?? []).Where(r => r.Id is not null)
                .GroupBy(r => r.Id!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            foreach (var result in run.Results ?? [])
            {
                var ruleId = result.RuleId ?? "";
                rules.TryGetValue(ruleId, out var rule);
                // A rule named after its own id says nothing; the result's
                // message then carries the check's name (and the target).
                var name = rule?.Name;
                if (string.IsNullOrWhiteSpace(name) || string.Equals(name, ruleId, StringComparison.Ordinal))
                    name = rule?.ShortDescription?.Text;
                if (string.IsNullOrWhiteSpace(name) || string.Equals(name, ruleId, StringComparison.Ordinal))
                    name = result.Message?.Text;
                findings.Add(new SecurityReportFinding(
                    RuleId: ruleId,
                    Title: string.IsNullOrWhiteSpace(name) ? ruleId : name,
                    Severity: SeverityFromLevel(result.Level),
                    OwaspApi: GetProp(rule, "owaspApi"),
                    Cvss: ParseCvss(GetProp(rule, "security-severity")),
                    Location: ExtractLocation(result),
                    Fingerprint: result.PartialFingerprints?.Values.FirstOrDefault() ?? "",
                    Remediation: rule?.FullDescription?.Text,
                    HelpUri: rule?.HelpUri));
            }
        }
        return findings;
    }

    private static string SeverityFromLevel(string? level) => level switch
    {
        "error" => "high",
        "warning" => "medium",
        "note" => "low",
        _ => "info",
    };

    private static string? GetProp(SarifRuleDto? rule, string key)
    {
        if (rule?.Properties is null || !rule.Properties.TryGetValue(key, out var el)) return null;
        var s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static double? ParseCvss(string? raw)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    // The logical location first: for a DAST result that is the scan
    // target, while the physical one is the placeholder Code Scanning
    // insists on — the workflow that ran the scan, which is not where
    // the finding is. A template result with no logical location keeps
    // its file.
    private static string ExtractLocation(SarifResultDto result)
    {
        var loc = result.Locations?.FirstOrDefault();
        if (loc is null) return "";
        var logical = loc.LogicalLocations?.FirstOrDefault()?.FullyQualifiedName;
        if (!string.IsNullOrEmpty(logical)) return logical;
        return loc.PhysicalLocation?.ArtifactLocation?.Uri ?? "";
    }

    // ---- minimal SARIF DTOs (only the fields the report needs) ----

    private sealed record SarifDoc([property: JsonPropertyName("runs")] List<SarifRunDto>? Runs);
    private sealed record SarifRunDto([property: JsonPropertyName("tool")] SarifToolDto? Tool, [property: JsonPropertyName("results")] List<SarifResultDto>? Results);
    private sealed record SarifToolDto([property: JsonPropertyName("driver")] SarifDriverDto? Driver);
    private sealed record SarifDriverDto([property: JsonPropertyName("rules")] List<SarifRuleDto>? Rules);
    private sealed record SarifRuleDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("shortDescription")] SarifMsgDto? ShortDescription,
        [property: JsonPropertyName("fullDescription")] SarifMsgDto? FullDescription,
        [property: JsonPropertyName("helpUri")] string? HelpUri,
        [property: JsonPropertyName("properties")] Dictionary<string, JsonElement>? Properties);
    private sealed record SarifResultDto(
        [property: JsonPropertyName("ruleId")] string? RuleId,
        [property: JsonPropertyName("level")] string? Level,
        [property: JsonPropertyName("message")] SarifMsgDto? Message,
        [property: JsonPropertyName("locations")] List<SarifLocDto>? Locations,
        [property: JsonPropertyName("partialFingerprints")] Dictionary<string, string>? PartialFingerprints);
    private sealed record SarifMsgDto([property: JsonPropertyName("text")] string? Text);
    private sealed record SarifLocDto(
        [property: JsonPropertyName("physicalLocation")] SarifPhysDto? PhysicalLocation,
        [property: JsonPropertyName("logicalLocations")] List<SarifLogicalDto>? LogicalLocations);
    private sealed record SarifPhysDto([property: JsonPropertyName("artifactLocation")] SarifArtifactDto? ArtifactLocation);
    private sealed record SarifArtifactDto([property: JsonPropertyName("uri")] string? Uri);
    private sealed record SarifLogicalDto([property: JsonPropertyName("fullyQualifiedName")] string? FullyQualifiedName);
}
