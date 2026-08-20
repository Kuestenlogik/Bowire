// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Kuestenlogik.Bowire.Reporting;

/// <summary>
/// The canonical JSON shape of a rollup, shared by every surface that emits
/// one (#587): <c>GET /api/report/rollup</c>, <c>bowire report rollup --json</c>
/// and the <c>bowire.report.rollup</c> MCP tool.
/// <para>
/// It exists for the same reason #364 grew one: serialising the model
/// directly let the CLI emit an enum ordinal where the endpoint emitted a
/// string, so a script could not treat the surfaces alike. One projection,
/// no drift.
/// </para>
/// </summary>
public static class BowireRollupPayload
{
    /// <summary>Project a rollup onto the wire shape.</summary>
    public static object ToWirePayload(BowireRollup rollup)
    {
        ArgumentNullException.ThrowIfNull(rollup);
        return new
        {
            services = rollup.Services.Select(s => new
            {
                service = s.Service,
                worst = SeverityText(s.Worst),
                lint = s.LintHigh is null && s.LintMedium is null && s.LintLow is null && s.LintInfo is null
                    ? null
                    : new
                    {
                        high = s.LintHigh ?? 0,
                        medium = s.LintMedium ?? 0,
                        low = s.LintLow ?? 0,
                        info = s.LintInfo ?? 0,
                    },
                contracts = s.ContractsTotal is null ? null : new
                {
                    passed = s.ContractsPassed ?? 0,
                    total = s.ContractsTotal ?? 0,
                },
                tests = s.TestsTotal is null ? null : new
                {
                    passed = s.TestsPassed ?? 0,
                    total = s.TestsTotal ?? 0,
                },
                benchmark = s.P95Ms is null && s.BenchmarkPassed is null ? null : new
                {
                    p95Ms = s.P95Ms,
                    passed = s.BenchmarkPassed,
                },
                scanErrors = s.ScanErrors,
                lastReportAt = s.LastReportAt,
                // Which files fed this row, so a surprising number is
                // traceable without re-running anything.
                sources = s.Sources.Select(src => new { kind = KindText(src.Kind), path = src.Path }),
            }),
            summary = new
            {
                services = rollup.Services.Count,
                atHigh = rollup.ServicesAtHigh,
                clean = rollup.ServicesClean,
                skipped = rollup.Skipped.Count,
            },
            skipped = rollup.Skipped.Select(s => new { path = s.Path, error = s.Error }),
        };
    }

    /// <summary>Wire spelling of a severity; <c>null</c> means nothing reportable.</summary>
    public static string? SeverityText(BowireRollupSeverity? severity) => severity switch
    {
        BowireRollupSeverity.High => "high",
        BowireRollupSeverity.Medium => "medium",
        BowireRollupSeverity.Low => "low",
        BowireRollupSeverity.Info => "info",
        _ => null,
    };

    /// <summary>Wire spelling of a report kind.</summary>
    public static string KindText(BowireReportKind kind) => kind switch
    {
        BowireReportKind.Lint => "lint",
        BowireReportKind.Contract => "contract",
        BowireReportKind.Benchmark => "benchmark",
        BowireReportKind.K6Summary => "k6-summary",
        BowireReportKind.Sarif => "sarif",
        BowireReportKind.JUnit => "junit",
        _ => "unknown",
    };

    /// <summary>
    /// Parse a <c>--fail-on</c> level. Returns null for "none" (the default,
    /// meaning never gate) and reports a typo rather than silently accepting
    /// it — a gate that quietly does nothing is worse than no gate.
    /// </summary>
    public static bool TryParseGate(string? text, out BowireRollupSeverity? level, out string? error)
    {
        level = null;
        error = null;
        var value = (text ?? "none").Trim().ToUpperInvariant();
        switch (value)
        {
            case "NONE": return true;
            case "INFO": level = BowireRollupSeverity.Info; return true;
            case "LOW": level = BowireRollupSeverity.Low; return true;
            case "MEDIUM": level = BowireRollupSeverity.Medium; return true;
            case "HIGH": level = BowireRollupSeverity.High; return true;
            default:
                error = $"'{text}' is not a severity — use none, info, low, medium or high";
                return false;
        }
    }

    /// <summary>Services at or above <paramref name="level"/>.</summary>
    public static int CountAtOrAbove(BowireRollup rollup, BowireRollupSeverity level)
    {
        ArgumentNullException.ThrowIfNull(rollup);
        return rollup.Services.Count(s => s.Worst is { } worst && worst >= level);
    }

    /// <summary>Compact cell text used by the CLI table (and handy in tests).</summary>
    public static string Cell(int? passed, int? total)
        => total is null ? "—" : $"{passed ?? 0}/{total}";

    /// <summary>Latency formatted for the CLI table.</summary>
    public static string Latency(double? ms)
        => ms is null ? "—"
            : ms >= 100 ? Math.Round(ms.Value).ToString("0", CultureInfo.InvariantCulture) + "ms"
            : ms.Value.ToString("0.##", CultureInfo.InvariantCulture) + "ms";
}
