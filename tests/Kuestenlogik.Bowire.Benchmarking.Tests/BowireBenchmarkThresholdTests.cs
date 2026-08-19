// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Kuestenlogik.Bowire.Benchmarking;

namespace Kuestenlogik.Bowire.Benchmarking.Tests;

/// <summary>
/// #360 — threshold specs are operator input from a command line or a CI
/// file, so parsing has to accept the spellings people actually write and
/// reject the rest with a usable message instead of throwing.
/// </summary>
public sealed class BowireBenchmarkThresholdTests
{
    [Theory]
    [InlineData("p95 < 200", BowireBenchmarkMetric.P95, BowireThresholdOperator.LessThan, 200)]
    [InlineData("p95<200", BowireBenchmarkMetric.P95, BowireThresholdOperator.LessThan, 200)]
    [InlineData("p50<=15.5", BowireBenchmarkMetric.P50, BowireThresholdOperator.LessThanOrEqual, 15.5)]
    [InlineData("p99 > 1", BowireBenchmarkMetric.P99, BowireThresholdOperator.GreaterThan, 1)]
    [InlineData("throughput >= 50", BowireBenchmarkMetric.Throughput, BowireThresholdOperator.GreaterThanOrEqual, 50)]
    [InlineData("error-rate < 0.01", BowireBenchmarkMetric.ErrorRate, BowireThresholdOperator.LessThan, 0.01)]
    public void TryParse_AcceptsTheCanonicalForms(
        string spec, BowireBenchmarkMetric metric, BowireThresholdOperator op, double budget)
    {
        Assert.True(BowireBenchmarkThreshold.TryParse(spec, out var t, out var error), error);
        Assert.NotNull(t);
        Assert.Equal(metric, t!.Metric);
        Assert.Equal(op, t.Operator);
        Assert.Equal(budget, t.Budget);
    }

    [Theory]
    [InlineData("p(95)<200")]      // k6's own spelling — a copied budget must work
    [InlineData("P95<200")]
    [InlineData("  p95  <  200  ")]
    public void TryParse_AcceptsK6AndSloppySpellings(string spec)
    {
        Assert.True(BowireBenchmarkThreshold.TryParse(spec, out var t, out _));
        Assert.Equal(BowireBenchmarkMetric.P95, t!.Metric);
        Assert.Equal(200, t.Budget);
    }

    [Theory]
    [InlineData("error_rate<0.1", BowireBenchmarkMetric.ErrorRate)]
    [InlineData("errors<0.1", BowireBenchmarkMetric.ErrorRate)]
    [InlineData("rps>10", BowireBenchmarkMetric.Throughput)]
    [InlineData("median<20", BowireBenchmarkMetric.P50)]
    [InlineData("mean<20", BowireBenchmarkMetric.Avg)]
    public void TryParse_AcceptsMetricAliases(string spec, BowireBenchmarkMetric expected)
    {
        Assert.True(BowireBenchmarkThreshold.TryParse(spec, out var t, out _));
        Assert.Equal(expected, t!.Metric);
    }

    [Fact]
    public void TryParse_PrefersTheTwoCharacterOperator()
    {
        // '<=' must not be read as '<' with a stray '=' in the budget.
        Assert.True(BowireBenchmarkThreshold.TryParse("p95<=200", out var t, out _));
        Assert.Equal(BowireThresholdOperator.LessThanOrEqual, t!.Operator);
        Assert.Equal(200, t.Budget);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("p95 200", "comparison")]
    [InlineData("p95<", "budget")]
    [InlineData("<200", "budget")]
    [InlineData("nonsense<200", "metric")]
    [InlineData("p95<abc", "number")]
    public void TryParse_RejectsBadInputWithAMessage(string spec, string expectedHint)
    {
        Assert.False(BowireBenchmarkThreshold.TryParse(spec, out var t, out var error));
        Assert.Null(t);
        Assert.NotNull(error);
        Assert.Contains(expectedHint, error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_ReadsDecimalsInvariantly()
    {
        // A CI file written with '0.05' must parse identically on a runner
        // whose culture uses a comma as the decimal separator.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.True(BowireBenchmarkThreshold.TryParse("error-rate<0.05", out var t, out _));
            Assert.Equal(0.05, t!.Budget);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Evaluate_PassesAndFailsAroundTheBudget()
    {
        var stats = BowireBenchmarkStats.From([10, 20, 30, 40, 200], errors: 0, elapsedSeconds: 1);

        Assert.True(BowireBenchmarkThreshold.TryParse("p50<=30", out var pass, out _));
        Assert.True(pass!.Evaluate(stats).Ok);

        Assert.True(BowireBenchmarkThreshold.TryParse("p50<10", out var fail, out _));
        var result = fail!.Evaluate(stats);
        Assert.False(result.Ok);
        Assert.Equal(stats.P50, result.Actual);
    }

    [Fact]
    public void Evaluate_StrictAndInclusiveDifferAtTheBoundary()
    {
        // Exactly at budget: '<' must fail, '<=' must pass. Off-by-one here
        // is the difference between a green and a red pipeline.
        var stats = BowireBenchmarkStats.From([100], errors: 0, elapsedSeconds: 1);

        Assert.True(BowireBenchmarkThreshold.TryParse("p95<100", out var strict, out _));
        Assert.False(strict!.Evaluate(stats).Ok);

        Assert.True(BowireBenchmarkThreshold.TryParse("p95<=100", out var inclusive, out _));
        Assert.True(inclusive!.Evaluate(stats).Ok);
    }

    [Fact]
    public void Evaluate_GatesOnErrorRateAndThroughput()
    {
        // 1 failure out of 5 calls in 2s → error rate 0.2, throughput 2.5/s.
        var stats = BowireBenchmarkStats.From([10, 10, 10, 10], errors: 1, elapsedSeconds: 2);

        Assert.True(BowireBenchmarkThreshold.TryParse("error-rate<0.1", out var errRate, out _));
        Assert.False(errRate!.Evaluate(stats).Ok);

        Assert.True(BowireBenchmarkThreshold.TryParse("throughput>=2", out var rps, out _));
        Assert.True(rps!.Evaluate(stats).Ok);
    }

    [Fact]
    public void ToString_RoundTripsThroughTheParser()
    {
        // The canonical spelling is the k6-summary key, so it must parse
        // back to the same threshold.
        Assert.True(BowireBenchmarkThreshold.TryParse("p95 < 200", out var t, out _));
        var canonical = t!.ToString();
        Assert.Equal("p95<200", canonical);

        Assert.True(BowireBenchmarkThreshold.TryParse(canonical, out var round, out _));
        Assert.Equal(t.Metric, round!.Metric);
        Assert.Equal(t.Operator, round.Operator);
        Assert.Equal(t.Budget, round.Budget);
    }
}
