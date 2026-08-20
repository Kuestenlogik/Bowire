// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Kuestenlogik.Bowire.Reporting;

/// <summary>
/// Reads the artefacts Bowire writes and folds them into per-service rows
/// (#587).
/// <para>
/// Deliberately parses the <em>file formats</em> rather than deserialising
/// into the producing packages' types. A rollup exists to answer a question
/// across services, so the reports come from other repositories, other
/// machines and other Bowire versions — assuming the package that wrote a
/// report is loaded in the process reading it would defeat the purpose. It
/// also means a field added upstream doesn't break the reader.
/// </para>
/// <para>
/// Format detection goes by content, not by file name: CI jobs name these
/// things whatever they like, and a <c>results.json</c> could be any of four
/// shapes.
/// </para>
/// </summary>
public static class BowireReportReader
{
    /// <summary>Directory names that are storage layout, never a service name.</summary>
    private static readonly string[] ArtefactDirectories =
        [".bowire", "contract-results", "benchmark-schedules", "reports", "artifacts", "artefacts"];

    /// <summary>
    /// Walk <paramref name="roots"/> (files or directories) and assemble the
    /// rollup. Every file is read independently: one unreadable or foreign
    /// file lands in <see cref="BowireRollup.Skipped"/> and the rest of the
    /// portfolio still reports.
    /// </summary>
    /// <param name="roots">Files or directories to read.</param>
    /// <param name="serviceOverride">Force every report onto this service name.</param>
    /// <param name="ct">Cancels the walk.</param>
    public static async Task<BowireRollup> ReadAsync(
        IEnumerable<string> roots, string? serviceOverride = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var byService = new Dictionary<string, BowireServiceReport>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<BowireReportSource>();

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (file, relativeRoot) in EnumerateFiles(root))
            {
                ct.ThrowIfCancellationRequested();
                string text;
                try
                {
                    text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(new BowireReportSource(BowireReportKind.Unknown, file, ex.Message));
                    continue;
                }

                var parsed = Parse(text, file);
                if (parsed is null)
                {
                    skipped.Add(new BowireReportSource(BowireReportKind.Unknown, file, "not a recognised Bowire report"));
                    continue;
                }

                var service = serviceOverride
                    ?? parsed.ServiceHint
                    ?? ServiceFromPath(file, relativeRoot);

                if (!byService.TryGetValue(service, out var row))
                {
                    row = new BowireServiceReport { Service = service };
                    byService[service] = row;
                }
                parsed.Apply(row);
                row.Sources.Add(new BowireReportSource(parsed.Kind, file));
                if (parsed.Timestamp is { } stamp && (row.LastReportAt is null || stamp > row.LastReportAt))
                {
                    row.LastReportAt = stamp;
                }
            }
        }

        return new BowireRollup
        {
            Services = byService.Values.OrderBy(s => s.Service, StringComparer.OrdinalIgnoreCase).ToList(),
            Skipped = skipped,
        };
    }

    private static IEnumerable<(string File, string Root)> EnumerateFiles(string root)
    {
        if (File.Exists(root))
        {
            yield return (root, Path.GetDirectoryName(root) ?? "");
            yield break;
        }
        if (!Directory.Exists(root)) yield break;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".sarif", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files) yield return (file, root);
    }

    /// <summary>
    /// Service name from where the file sits: the first path segment under
    /// the scanned root that isn't storage layout. <c>reports/orders-api/lint.json</c>
    /// yields <c>orders-api</c>; a report sitting loose falls back to its own
    /// file name, which is at least stable and visible.
    /// </summary>
    internal static string ServiceFromPath(string file, string root)
    {
        try
        {
            var relative = string.IsNullOrEmpty(root)
                ? Path.GetFileName(file)
                : Path.GetRelativePath(root, file);
            var segments = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments[..^1])
            {
                if (!ArtefactDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)) return segment;
            }
        }
        catch (ArgumentException)
        {
            // Unrelated roots on different volumes — fall through to the name.
        }
        return Path.GetFileNameWithoutExtension(file);
    }

    /// <summary>What one file contributed, before it is folded into a row.</summary>
    private sealed record Parsed(
        BowireReportKind Kind,
        Action<BowireServiceReport> Apply,
        string? ServiceHint = null,
        DateTime? Timestamp = null);

    private static Parsed? Parse(string text, string path)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('<')) return ParseXml(text);

        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (JsonException) { return null; }
        if (node is null) return null;

        return ParseLint(node)
            ?? ParseContract(node)
            ?? ParseBenchmarkRuns(node)
            ?? ParseK6(node)
            ?? ParseSarif(node);
    }

    // ---- lint: { findings: [{ severity, … }], summary: { high, medium, … } }

    private static Parsed? ParseLint(JsonNode node)
    {
        if (node is not JsonObject obj || obj["findings"] is not JsonArray findings) return null;

        // Count from the findings themselves rather than trusting `summary`:
        // the array is the ground truth and older reports may not carry one.
        int high = 0, medium = 0, low = 0, info = 0;
        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var finding in findings.OfType<JsonObject>())
        {
            switch ((finding["severity"]?.GetValue<string>() ?? "").ToUpperInvariant())
            {
                case "HIGH": high++; break;
                case "MEDIUM": medium++; break;
                case "LOW": low++; break;
                case "INFO": info++; break;
                default: break;
            }
            var service = finding["service"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(service)) services.Add(service);
        }

        // Only adopt the finding's service when the whole report is about one
        // service; a mixed report belongs to whatever the path says.
        var hint = services.Count == 1 ? services.First() : null;

        return new Parsed(BowireReportKind.Lint, row =>
        {
            row.LintHigh = (row.LintHigh ?? 0) + high;
            row.LintMedium = (row.LintMedium ?? 0) + medium;
            row.LintLow = (row.LintLow ?? 0) + low;
            row.LintInfo = (row.LintInfo ?? 0) + info;
        }, hint);
    }

    // ---- contract: { consumer, provider, interactions: [...], failedInteractions }

    private static Parsed? ParseContract(JsonNode node)
    {
        if (node is not JsonObject obj) return null;
        if (obj["provider"] is null || obj["interactions"] is not JsonArray) return null;

        var provider = obj["provider"]?.GetValue<string>();
        var failed = obj["failedInteractions"]?.GetValue<int>() ?? 0;
        var startedAt = ReadDate(obj["startedAt"]);

        return new Parsed(BowireReportKind.Contract, row =>
        {
            row.ContractsTotal = (row.ContractsTotal ?? 0) + 1;
            row.ContractsPassed = (row.ContractsPassed ?? 0) + (failed == 0 ? 1 : 0);
        },
        // The provider is the service under test — that is whose rollup row
        // a broken contract belongs in.
        string.IsNullOrWhiteSpace(provider) ? null : provider,
        startedAt);
    }

    // ---- benchmark schedule history: [ { scheduleId, p95, passed, … }, … ]

    private static Parsed? ParseBenchmarkRuns(JsonNode node)
    {
        if (node is not JsonArray array) return null;
        var newest = array.OfType<JsonObject>()
            .Where(o => o["p95"] is not null && o["scheduleId"] is not null)
            .OrderByDescending(o => ReadDate(o["startedAt"]) ?? DateTime.MinValue)
            .FirstOrDefault();
        if (newest is null) return null;

        var p95 = newest["p95"]?.GetValue<double>();
        var passed = newest["passed"]?.GetValue<bool>();
        var startedAt = ReadDate(newest["startedAt"]);

        return new Parsed(BowireReportKind.Benchmark, row =>
        {
            // Newest wins across files; a service with several schedules
            // reports its slowest-known-latest rather than an average that
            // would hide a regression in one of them.
            if (row.P95Ms is null || p95 > row.P95Ms) row.P95Ms = p95;
            if (passed == false || row.BenchmarkPassed is null) row.BenchmarkPassed = passed;
        }, null, startedAt);
    }

    // ---- k6 summary: { metrics: { http_req_duration: { values: { "p(95)": … } } } }

    private static Parsed? ParseK6(JsonNode node)
    {
        if (node is not JsonObject obj || obj["metrics"] is not JsonObject metrics) return null;
        if (metrics["http_req_duration"] is not JsonObject duration) return null;
        var p95 = (duration["values"] as JsonObject)?["p(95)"]?.GetValue<double>();
        if (p95 is null) return null;

        // A k6 export carries its threshold verdicts inline (#360).
        var allOk = true;
        foreach (var metric in metrics.OfType<KeyValuePair<string, JsonNode?>>())
        {
            if (metric.Value is not JsonObject m || m["thresholds"] is not JsonObject thresholds) continue;
            foreach (var threshold in thresholds.OfType<KeyValuePair<string, JsonNode?>>())
            {
                if ((threshold.Value as JsonObject)?["ok"]?.GetValue<bool>() == false) allOk = false;
            }
        }

        return new Parsed(BowireReportKind.K6Summary, row =>
        {
            if (row.P95Ms is null || p95 > row.P95Ms) row.P95Ms = p95;
            if (!allOk || row.BenchmarkPassed is null) row.BenchmarkPassed = allOk;
        });
    }

    // ---- SARIF: { runs: [ { results: [ { level: "error" } ] } ] }

    private static Parsed? ParseSarif(JsonNode node)
    {
        if (node is not JsonObject obj || obj["runs"] is not JsonArray runs) return null;

        var errors = 0;
        foreach (var run in runs.OfType<JsonObject>())
        {
            if (run["results"] is not JsonArray results) continue;
            errors += results.OfType<JsonObject>()
                .Count(r => string.Equals(r["level"]?.GetValue<string>(), "error", StringComparison.OrdinalIgnoreCase));
        }

        return new Parsed(BowireReportKind.Sarif, row => row.ScanErrors = (row.ScanErrors ?? 0) + errors);
    }

    // ---- JUnit: <testsuites tests="…" failures="…">

    private static Parsed? ParseXml(string text)
    {
        XDocument doc;
        try { doc = XDocument.Parse(text); }
        catch (System.Xml.XmlException) { return null; }

        var suites = doc.Descendants("testsuite").ToList();
        if (suites.Count == 0 && doc.Root?.Name.LocalName != "testsuites") return null;

        var total = 0;
        var failures = 0;
        foreach (var suite in suites)
        {
            total += ReadInt(suite.Attribute("tests")?.Value);
            failures += ReadInt(suite.Attribute("failures")?.Value) + ReadInt(suite.Attribute("errors")?.Value);
        }
        if (total == 0 && doc.Root is { } root)
        {
            total = ReadInt(root.Attribute("tests")?.Value);
            failures = ReadInt(root.Attribute("failures")?.Value) + ReadInt(root.Attribute("errors")?.Value);
        }
        if (total == 0) return null;

        var passed = Math.Max(0, total - failures);
        return new Parsed(BowireReportKind.JUnit, row =>
        {
            row.TestsTotal = (row.TestsTotal ?? 0) + total;
            row.TestsPassed = (row.TestsPassed ?? 0) + passed;
        });
    }

    private static int ReadInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static DateTime? ReadDate(JsonNode? node)
    {
        var text = node?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}
