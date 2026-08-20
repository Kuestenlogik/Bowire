// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Reporting;

/// <summary>Kind of artefact a rollup row was fed by (#587).</summary>
public enum BowireReportKind
{
    /// <summary>Not recognised as any Bowire report.</summary>
    Unknown,

    /// <summary>`bowire lint --format json` findings.</summary>
    Lint,

    /// <summary>A contract-verification result (#364).</summary>
    Contract,

    /// <summary>A scheduled-benchmark run history (#232).</summary>
    Benchmark,

    /// <summary>A k6-summary export (#360).</summary>
    K6Summary,

    /// <summary>`bowire scan` SARIF.</summary>
    Sarif,

    /// <summary>`bowire test --junit` XML.</summary>
    JUnit,
}

/// <summary>
/// One artefact the rollup read, kept alongside the row it fed so a reader
/// can trace a number back to the file it came from (#587).
/// </summary>
/// <param name="Kind">What the file turned out to be.</param>
/// <param name="Path">Where it was read from.</param>
/// <param name="Error">Why it could not be used, when it could not.</param>
public sealed record BowireReportSource(BowireReportKind Kind, string Path, string? Error = null);

/// <summary>
/// The rolled-up state of one service (#587) — what a platform team asks
/// about a portfolio: is anything failing, how bad, and how fresh is the
/// answer.
/// <para>
/// Every count is nullable on purpose: a service with no lint report at all
/// is a different statement from one with zero findings, and flattening both
/// to 0 would let a missing report read as a clean bill of health.
/// </para>
/// </summary>
public sealed class BowireServiceReport
{
    /// <summary>Service this row is about.</summary>
    public required string Service { get; init; }

    /// <summary>Lint findings by severity, when a lint report was read.</summary>
    public int? LintHigh { get; set; }

    /// <summary>Medium-severity lint findings.</summary>
    public int? LintMedium { get; set; }

    /// <summary>Low-severity lint findings.</summary>
    public int? LintLow { get; set; }

    /// <summary>Info-level lint findings.</summary>
    public int? LintInfo { get; set; }

    /// <summary>Contract verifications that held.</summary>
    public int? ContractsPassed { get; set; }

    /// <summary>Contract verifications read in total.</summary>
    public int? ContractsTotal { get; set; }

    /// <summary>Most recent p95 latency in milliseconds, from a benchmark run or k6 summary.</summary>
    public double? P95Ms { get; set; }

    /// <summary>Whether the latest benchmark run stayed inside its thresholds.</summary>
    public bool? BenchmarkPassed { get; set; }

    /// <summary>Tests that passed.</summary>
    public int? TestsPassed { get; set; }

    /// <summary>Tests run in total.</summary>
    public int? TestsTotal { get; set; }

    /// <summary>Scan findings at error level.</summary>
    public int? ScanErrors { get; set; }

    /// <summary>Newest timestamp seen across this service's reports.</summary>
    public DateTime? LastReportAt { get; set; }

    /// <summary>The files this row was built from, including the ones that failed to parse.</summary>
    public List<BowireReportSource> Sources { get; init; } = [];

    /// <summary>
    /// Highest severity this service is carrying, or null when nothing
    /// reportable was found. Drives the <c>--fail-on</c> gate and the rail's
    /// row colour.
    /// </summary>
    public BowireRollupSeverity? Worst
    {
        get
        {
            if (LintHigh > 0 || ScanErrors > 0) return BowireRollupSeverity.High;
            // A broken contract or a breached latency budget is a real
            // regression, not a style note — medium, above any lint medium.
            if (ContractsTotal is > 0 && ContractsPassed < ContractsTotal) return BowireRollupSeverity.High;
            if (TestsTotal is > 0 && TestsPassed < TestsTotal) return BowireRollupSeverity.High;
            if (BenchmarkPassed == false) return BowireRollupSeverity.Medium;
            if (LintMedium > 0) return BowireRollupSeverity.Medium;
            if (LintLow > 0) return BowireRollupSeverity.Low;
            if (LintInfo > 0) return BowireRollupSeverity.Info;
            return null;
        }
    }
}

/// <summary>Severity ladder the rollup gate compares against (#587).</summary>
public enum BowireRollupSeverity
{
    /// <summary>Nothing reportable. Present so the enum has a zero value
    /// (CA1008); the rollup itself expresses "clean" as a null severity.</summary>
    None = 0,

    /// <summary>Informational only.</summary>
    Info = 1,

    /// <summary>Low severity.</summary>
    Low = 2,

    /// <summary>Medium severity.</summary>
    Medium = 3,

    /// <summary>High severity — a failing contract, test or scan finding.</summary>
    High = 4,
}

/// <summary>The assembled portfolio view (#587).</summary>
public sealed class BowireRollup
{
    /// <summary>One row per service, sorted by name.</summary>
    public required IReadOnlyList<BowireServiceReport> Services { get; init; }

    /// <summary>Files that were read but not recognised or not parseable.</summary>
    public required IReadOnlyList<BowireReportSource> Skipped { get; init; }

    /// <summary>Services carrying at least one high-severity signal.</summary>
    public int ServicesAtHigh => Services.Count(s => s.Worst == BowireRollupSeverity.High);

    /// <summary>Services with nothing reportable.</summary>
    public int ServicesClean => Services.Count(s => s.Worst is null);
}
