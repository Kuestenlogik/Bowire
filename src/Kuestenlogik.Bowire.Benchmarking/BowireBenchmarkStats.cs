// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Benchmarking;

/// <summary>
/// Aggregate latency statistics over one benchmark run (#360).
/// <para>
/// Percentiles use <b>nearest-rank</b> — <c>ceil(p/100 × n) − 1</c> into the
/// sorted sample — which is the method the workbench's benchmark rail has
/// always used (<c>_percentileSorted</c> in benchmarks.js). Matching it
/// matters more than picking a "better" interpolation: an operator who sees
/// p95 = 142 ms in the rail and then gates CI on <c>p95 &lt; 150</c> must not
/// get a different number from the CLI for the same sample.
/// </para>
/// </summary>
public sealed class BowireBenchmarkStats
{
    private BowireBenchmarkStats() { }

    /// <summary>Number of completed calls the sample covers.</summary>
    public int Count { get; private init; }

    /// <summary>Calls that failed (transport error or error status).</summary>
    public int Errors { get; private init; }

    /// <summary>Failed calls as a fraction of all calls (0..1) — k6's <c>http_req_failed</c> rate.</summary>
    public double ErrorRate { get; private init; }

    /// <summary>Completed calls per second over the run's wall clock.</summary>
    public double Throughput { get; private init; }

    /// <summary>Arithmetic mean latency in milliseconds.</summary>
    public double Avg { get; private init; }

    /// <summary>Fastest observed latency in milliseconds.</summary>
    public double Min { get; private init; }

    /// <summary>Slowest observed latency in milliseconds.</summary>
    public double Max { get; private init; }

    /// <summary>Median latency in milliseconds.</summary>
    public double P50 { get; private init; }

    /// <summary>90th-percentile latency in milliseconds.</summary>
    public double P90 { get; private init; }

    /// <summary>95th-percentile latency in milliseconds.</summary>
    public double P95 { get; private init; }

    /// <summary>99th-percentile latency in milliseconds.</summary>
    public double P99 { get; private init; }

    /// <summary>The sorted latency sample the percentiles were taken from.</summary>
    public IReadOnlyList<double> SortedLatencies { get; private init; } = [];

    /// <summary>
    /// Compute the statistics for a run. <paramref name="latenciesMs"/> may
    /// arrive in any order — it is sorted here, once, so every percentile
    /// reads from the same canonical array.
    /// </summary>
    /// <param name="latenciesMs">Latency of each completed call, in milliseconds.</param>
    /// <param name="errors">How many calls failed.</param>
    /// <param name="elapsedSeconds">Wall-clock duration of the run, for throughput.</param>
    public static BowireBenchmarkStats From(IEnumerable<double> latenciesMs, int errors, double elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(latenciesMs);

        var sorted = latenciesMs.ToArray();
        Array.Sort(sorted);

        // Errors are counted separately from the latency sample: a call that
        // never completed has no latency to fold in, but it still has to
        // move the error rate. Total = completed + failed.
        var total = sorted.Length + errors;

        return new BowireBenchmarkStats
        {
            Count = sorted.Length,
            Errors = errors,
            ErrorRate = total > 0 ? (double)errors / total : 0,
            Throughput = elapsedSeconds > 0 ? total / elapsedSeconds : 0,
            Avg = sorted.Length > 0 ? sorted.Average() : 0,
            Min = sorted.Length > 0 ? sorted[0] : 0,
            Max = sorted.Length > 0 ? sorted[^1] : 0,
            P50 = PercentileOfSorted(sorted, 50),
            P90 = PercentileOfSorted(sorted, 90),
            P95 = PercentileOfSorted(sorted, 95),
            P99 = PercentileOfSorted(sorted, 99),
            SortedLatencies = sorted,
        };
    }

    /// <summary>
    /// Nearest-rank percentile over an already-sorted sample. Mirrors
    /// <c>_percentileSorted</c> in the benchmark rail's JS, including the
    /// empty-sample answer of 0 and the clamp at both ends.
    /// </summary>
    public static double PercentileOfSorted(IReadOnlyList<double> sorted, double percentile)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0) return 0;
        var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
    }

    /// <summary>
    /// Read one metric by its <see cref="BowireBenchmarkMetric"/> id — the
    /// seam thresholds evaluate through, so adding a metric doesn't mean
    /// teaching the threshold evaluator about it separately.
    /// </summary>
    public double Value(BowireBenchmarkMetric metric) => metric switch
    {
        BowireBenchmarkMetric.P50 => P50,
        BowireBenchmarkMetric.P90 => P90,
        BowireBenchmarkMetric.P95 => P95,
        BowireBenchmarkMetric.P99 => P99,
        BowireBenchmarkMetric.Avg => Avg,
        BowireBenchmarkMetric.Min => Min,
        BowireBenchmarkMetric.Max => Max,
        BowireBenchmarkMetric.ErrorRate => ErrorRate,
        BowireBenchmarkMetric.Throughput => Throughput,
        _ => 0,
    };
}
