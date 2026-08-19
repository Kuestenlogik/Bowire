// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Kuestenlogik.Bowire.Benchmarking;

/// <summary>A metric a threshold can be set on (#360).</summary>
public enum BowireBenchmarkMetric
{
    /// <summary>Median latency, milliseconds.</summary>
    P50,

    /// <summary>90th-percentile latency, milliseconds.</summary>
    P90,

    /// <summary>95th-percentile latency, milliseconds.</summary>
    P95,

    /// <summary>99th-percentile latency, milliseconds.</summary>
    P99,

    /// <summary>Mean latency, milliseconds.</summary>
    Avg,

    /// <summary>Fastest call, milliseconds.</summary>
    Min,

    /// <summary>Slowest call, milliseconds.</summary>
    Max,

    /// <summary>Failed calls as a fraction of all calls (0..1).</summary>
    ErrorRate,

    /// <summary>Completed calls per second.</summary>
    Throughput,
}

/// <summary>Comparison a threshold applies between the measured value and the budget.</summary>
public enum BowireThresholdOperator
{
    /// <summary>Measured value must be below the budget.</summary>
    LessThan,

    /// <summary>Measured value must be at most the budget.</summary>
    LessThanOrEqual,

    /// <summary>Measured value must exceed the budget.</summary>
    GreaterThan,

    /// <summary>Measured value must be at least the budget.</summary>
    GreaterThanOrEqual,
}

/// <summary>
/// A latency / error / throughput budget a benchmark run must satisfy
/// (#360) — k6's threshold concept, expressed the same way flow
/// expectations are, so an operator learns one grammar.
/// <para>
/// Written as <c>metric operator budget</c>: <c>p95 &lt; 200</c>,
/// <c>error-rate &lt; 0.01</c>, <c>throughput &gt;= 50</c>. Whitespace is
/// optional and k6's own <c>p(95)</c> spelling parses too, so a budget
/// copied out of a k6 script keeps working.
/// </para>
/// </summary>
public sealed class BowireBenchmarkThreshold
{
    private BowireBenchmarkThreshold() { }

    /// <summary>The metric under budget.</summary>
    public BowireBenchmarkMetric Metric { get; private init; }

    /// <summary>How the measured value is compared to the budget.</summary>
    public BowireThresholdOperator Operator { get; private init; }

    /// <summary>The budget itself — milliseconds, a 0..1 rate, or calls/second.</summary>
    public double Budget { get; private init; }

    /// <summary>The spec as the operator wrote it, for messages and the k6 export.</summary>
    public string Source { get; private init; } = "";

    /// <summary>
    /// Parse a threshold spec. Returns false with a human-readable
    /// <paramref name="error"/> rather than throwing: these come from a
    /// command line, so a typo deserves a usage message, not a stack trace.
    /// </summary>
    public static bool TryParse(string? spec, out BowireBenchmarkThreshold? threshold, out string? error)
    {
        threshold = null;
        error = null;

        if (string.IsNullOrWhiteSpace(spec))
        {
            error = "empty threshold spec";
            return false;
        }

        var text = spec.Replace(" ", "", StringComparison.Ordinal);

        // Longest operators first: '<=' must win over '<'.
        var (op, opText) = text.Contains("<=", StringComparison.Ordinal) ? (BowireThresholdOperator.LessThanOrEqual, "<=")
            : text.Contains(">=", StringComparison.Ordinal) ? (BowireThresholdOperator.GreaterThanOrEqual, ">=")
            : text.Contains('<', StringComparison.Ordinal) ? (BowireThresholdOperator.LessThan, "<")
            : text.Contains('>', StringComparison.Ordinal) ? (BowireThresholdOperator.GreaterThan, ">")
            : (default, null);

        if (opText is null)
        {
            error = $"'{spec}' has no comparison — expected something like 'p95 < 200'";
            return false;
        }

        var parts = text.Split(opText, StringSplitOptions.None);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            error = $"'{spec}' is not 'metric {opText} budget'";
            return false;
        }

        if (!TryParseMetric(parts[0], out var metric))
        {
            error = $"'{parts[0]}' is not a known metric — use p50 / p90 / p95 / p99 / avg / min / max / error-rate / throughput";
            return false;
        }

        // Invariant culture on purpose: a threshold is machine input from a
        // CI file, so '0.01' must parse the same on a German-locale runner.
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var budget))
        {
            error = $"'{parts[1]}' is not a number";
            return false;
        }

        threshold = new BowireBenchmarkThreshold
        {
            Metric = metric,
            Operator = op,
            Budget = budget,
            Source = spec.Trim(),
        };
        return true;
    }

    private static bool TryParseMetric(string text, out BowireBenchmarkMetric metric)
    {
        // k6 writes percentiles as p(95); '-' and '_' both appear in the
        // wild for error-rate. Normalise before matching so every spelling
        // an operator might reasonably type lands on the same metric.
        // Upper-cased rather than lower: casing down is lossy in some
        // cultures, which is what CA1308 guards against.
        var key = text.Replace("(", "", StringComparison.Ordinal)
                      .Replace(")", "", StringComparison.Ordinal)
                      .Replace("-", "", StringComparison.Ordinal)
                      .Replace("_", "", StringComparison.Ordinal)
                      .ToUpperInvariant();

        switch (key)
        {
            case "P50": case "MED": case "MEDIAN": metric = BowireBenchmarkMetric.P50; return true;
            case "P90": metric = BowireBenchmarkMetric.P90; return true;
            case "P95": metric = BowireBenchmarkMetric.P95; return true;
            case "P99": metric = BowireBenchmarkMetric.P99; return true;
            case "AVG": case "MEAN": metric = BowireBenchmarkMetric.Avg; return true;
            case "MIN": metric = BowireBenchmarkMetric.Min; return true;
            case "MAX": metric = BowireBenchmarkMetric.Max; return true;
            case "ERRORRATE": case "ERRORS": case "HTTPREQFAILED": metric = BowireBenchmarkMetric.ErrorRate; return true;
            case "THROUGHPUT": case "RPS": case "REQS": metric = BowireBenchmarkMetric.Throughput; return true;
            default: metric = default; return false;
        }
    }

    /// <summary>Check the threshold against a run's statistics.</summary>
    public BowireThresholdResult Evaluate(BowireBenchmarkStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var actual = stats.Value(Metric);
        var ok = Operator switch
        {
            BowireThresholdOperator.LessThan => actual < Budget,
            BowireThresholdOperator.LessThanOrEqual => actual <= Budget,
            BowireThresholdOperator.GreaterThan => actual > Budget,
            BowireThresholdOperator.GreaterThanOrEqual => actual >= Budget,
            _ => true,
        };
        return new BowireThresholdResult(this, actual, ok);
    }

    /// <summary>
    /// The canonical spelling, used in the TTY summary and as the key in
    /// the k6-summary export (k6 keys thresholds by their source text).
    /// </summary>
    public override string ToString()
    {
        var op = Operator switch
        {
            BowireThresholdOperator.LessThan => "<",
            BowireThresholdOperator.LessThanOrEqual => "<=",
            BowireThresholdOperator.GreaterThan => ">",
            BowireThresholdOperator.GreaterThanOrEqual => ">=",
            _ => "?",
        };
        return $"{MetricName(Metric)}{op}{Budget.ToString("G", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Wire / display name of a metric.</summary>
    public static string MetricName(BowireBenchmarkMetric metric) => metric switch
    {
        BowireBenchmarkMetric.P50 => "p50",
        BowireBenchmarkMetric.P90 => "p90",
        BowireBenchmarkMetric.P95 => "p95",
        BowireBenchmarkMetric.P99 => "p99",
        BowireBenchmarkMetric.Avg => "avg",
        BowireBenchmarkMetric.Min => "min",
        BowireBenchmarkMetric.Max => "max",
        BowireBenchmarkMetric.ErrorRate => "error-rate",
        BowireBenchmarkMetric.Throughput => "throughput",
        // Every member is named above; the arm exists only to satisfy the
        // compiler's exhaustiveness check for a cast-in enum value.
        _ => metric.ToString(),
    };

    /// <summary>True when the metric is a latency in milliseconds (for formatting).</summary>
    public static bool IsLatency(BowireBenchmarkMetric metric) => metric
        is BowireBenchmarkMetric.P50 or BowireBenchmarkMetric.P90 or BowireBenchmarkMetric.P95
        or BowireBenchmarkMetric.P99 or BowireBenchmarkMetric.Avg or BowireBenchmarkMetric.Min
        or BowireBenchmarkMetric.Max;
}

/// <summary>Outcome of checking one threshold against a run.</summary>
/// <param name="Threshold">The budget that was checked.</param>
/// <param name="Actual">The measured value.</param>
/// <param name="Ok">Whether the run stayed within budget.</param>
public sealed record BowireThresholdResult(BowireBenchmarkThreshold Threshold, double Actual, bool Ok);
