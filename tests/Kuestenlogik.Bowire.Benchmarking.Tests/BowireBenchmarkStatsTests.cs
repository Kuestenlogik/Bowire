// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Benchmarking;

namespace Kuestenlogik.Bowire.Benchmarking.Tests;

/// <summary>
/// #360 — the aggregation the CLI gates on. The percentile method has to
/// match the workbench rail's (<c>_percentileSorted</c>, nearest-rank), or
/// an operator reading p95 in the UI and gating on it in CI gets two
/// different numbers for the same sample.
/// </summary>
public sealed class BowireBenchmarkStatsTests
{
    [Fact]
    public void Percentile_UsesNearestRankLikeTheWorkbench()
    {
        // JS: idx = ceil(p/100 * n) - 1 over [10,20,…,100].
        // p95 → ceil(9.5)-1 = 9 → 100. p50 → ceil(5)-1 = 4 → 50.
        double[] sorted = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        Assert.Equal(50, BowireBenchmarkStats.PercentileOfSorted(sorted, 50));
        Assert.Equal(90, BowireBenchmarkStats.PercentileOfSorted(sorted, 90));
        Assert.Equal(100, BowireBenchmarkStats.PercentileOfSorted(sorted, 95));
        Assert.Equal(100, BowireBenchmarkStats.PercentileOfSorted(sorted, 99));
    }

    [Fact]
    public void Percentile_ClampsAndHandlesTheEmptySample()
    {
        Assert.Equal(0, BowireBenchmarkStats.PercentileOfSorted([], 95));
        Assert.Equal(7, BowireBenchmarkStats.PercentileOfSorted([7], 1));
        Assert.Equal(7, BowireBenchmarkStats.PercentileOfSorted([7], 100));
    }

    [Fact]
    public void From_SortsTheSampleItself()
    {
        // Callers hand latencies over in completion order, not sorted.
        var stats = BowireBenchmarkStats.From([50, 10, 90, 30], errors: 0, elapsedSeconds: 1);
        Assert.Equal(10, stats.Min);
        Assert.Equal(90, stats.Max);
        Assert.Equal(45, stats.Avg);
        Assert.Equal([10, 30, 50, 90], stats.SortedLatencies);
    }

    [Fact]
    public void From_CountsErrorsOutsideTheLatencySample()
    {
        // 4 completed + 1 failed = 5 calls: the failure moves the error
        // rate but must not enter the percentiles.
        var stats = BowireBenchmarkStats.From([10, 10, 10, 10], errors: 1, elapsedSeconds: 2);
        Assert.Equal(4, stats.Count);
        Assert.Equal(1, stats.Errors);
        Assert.Equal(0.2, stats.ErrorRate, 6);
        Assert.Equal(2.5, stats.Throughput, 6); // 5 calls / 2s
        Assert.Equal(10, stats.P95);
    }

    [Fact]
    public void From_EmptyRunIsAllZeroesRatherThanNaN()
    {
        // Dividing by a zero sample or zero elapsed must not produce NaN —
        // a NaN would serialise into the k6 summary as null and break
        // downstream tooling.
        var stats = BowireBenchmarkStats.From([], errors: 0, elapsedSeconds: 0);
        Assert.Equal(0, stats.Count);
        Assert.Equal(0, stats.ErrorRate);
        Assert.Equal(0, stats.Throughput);
        Assert.Equal(0, stats.Avg);
        Assert.Equal(0, stats.P95);
    }

    [Fact]
    public void Value_ReadsEveryMetricThresholdsCanGateOn()
    {
        var stats = BowireBenchmarkStats.From([10, 20, 30, 40], errors: 1, elapsedSeconds: 5);
        Assert.Equal(stats.P50, stats.Value(BowireBenchmarkMetric.P50));
        Assert.Equal(stats.P90, stats.Value(BowireBenchmarkMetric.P90));
        Assert.Equal(stats.P95, stats.Value(BowireBenchmarkMetric.P95));
        Assert.Equal(stats.P99, stats.Value(BowireBenchmarkMetric.P99));
        Assert.Equal(stats.Avg, stats.Value(BowireBenchmarkMetric.Avg));
        Assert.Equal(stats.Min, stats.Value(BowireBenchmarkMetric.Min));
        Assert.Equal(stats.Max, stats.Value(BowireBenchmarkMetric.Max));
        Assert.Equal(stats.ErrorRate, stats.Value(BowireBenchmarkMetric.ErrorRate));
        Assert.Equal(stats.Throughput, stats.Value(BowireBenchmarkMetric.Throughput));
    }
}

/// <summary>
/// #360 — thresholds have to land in the k6-summary export where a k6
/// dashboard already looks for them, and the metric block has to keep the
/// shape the workbench rail exports (#234).
/// </summary>
public sealed class BowireK6SummaryTests
{
    private static BowireBenchmarkStats Sample()
        => BowireBenchmarkStats.From([10, 20, 30, 40], errors: 1, elapsedSeconds: 2);

    [Fact]
    public void Build_KeepsTheRailsMetricShape()
    {
        var doc = BowireK6Summary.Build(Sample());
        var metrics = Assert.IsType<JsonObject>(doc["metrics"]);

        foreach (var name in new[] { "http_req_duration", "http_reqs", "iterations", "iteration_duration", "http_req_failed", "checks" })
        {
            Assert.True(metrics.ContainsKey(name), $"missing metric {name}");
        }

        var duration = Assert.IsType<JsonObject>(metrics["http_req_duration"]);
        Assert.Equal("trend", duration["type"]!.GetValue<string>());
        Assert.Equal("time", duration["contains"]!.GetValue<string>());
        var values = Assert.IsType<JsonObject>(duration["values"]);
        // k6 spells percentiles p(95) — a dashboard keys off exactly that.
        foreach (var key in new[] { "avg", "min", "max", "med", "p(90)", "p(95)", "p(99)", "count" })
        {
            Assert.True(values.ContainsKey(key), $"missing value {key}");
        }
    }

    [Fact]
    public void Build_InvertsHttpReqFailedLikeK6()
    {
        // k6 counts failures as the 'passes' of the failure rate; the rail
        // export already follows that, so the CLI must not "fix" it.
        var doc = BowireK6Summary.Build(Sample());
        var failed = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(doc["metrics"])["http_req_failed"]);
        var values = Assert.IsType<JsonObject>(failed["values"]);
        Assert.Equal(1, values["passes"]!.GetValue<int>());  // the failure
        Assert.Equal(4, values["fails"]!.GetValue<int>());   // the successes
        Assert.Equal(0.2, values["rate"]!.GetValue<double>(), 6);
    }

    [Fact]
    public void Build_HangsLatencyThresholdsOffHttpReqDuration()
    {
        var stats = Sample();
        Assert.True(BowireBenchmarkThreshold.TryParse("p95<100", out var t, out _));
        var doc = BowireK6Summary.Build(stats, [t!.Evaluate(stats)]);

        var duration = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(doc["metrics"])["http_req_duration"]);
        var thresholds = Assert.IsType<JsonObject>(duration["thresholds"]);
        var entry = Assert.IsType<JsonObject>(thresholds["p95<100"]);
        Assert.True(entry["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void Build_RoutesErrorRateAndThroughputToTheirOwnMetrics()
    {
        var stats = Sample();
        Assert.True(BowireBenchmarkThreshold.TryParse("error-rate<0.1", out var err, out _));
        Assert.True(BowireBenchmarkThreshold.TryParse("throughput>1", out var rps, out _));
        var doc = BowireK6Summary.Build(stats, [err!.Evaluate(stats), rps!.Evaluate(stats)]);
        var metrics = Assert.IsType<JsonObject>(doc["metrics"]);

        var failedThresholds = Assert.IsType<JsonObject>(
            Assert.IsType<JsonObject>(metrics["http_req_failed"])["thresholds"]);
        Assert.False(Assert.IsType<JsonObject>(failedThresholds["error-rate<0.1"])["ok"]!.GetValue<bool>());

        var reqThresholds = Assert.IsType<JsonObject>(
            Assert.IsType<JsonObject>(metrics["http_reqs"])["thresholds"]);
        Assert.True(Assert.IsType<JsonObject>(reqThresholds["throughput>1"])["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void Build_WithoutThresholds_OmitsTheBlock()
    {
        // A run with no budgets must not grow an empty thresholds object —
        // k6 doesn't emit one either.
        var doc = BowireK6Summary.Build(Sample());
        var duration = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(doc["metrics"])["http_req_duration"]);
        Assert.False(duration.ContainsKey("thresholds"));
    }

    [Fact]
    public void Render_ProducesParseableJson()
    {
        var stats = Sample();
        Assert.True(BowireBenchmarkThreshold.TryParse("p95<100", out var t, out _));
        var json = BowireK6Summary.Render(stats, [t!.Evaluate(stats)]);
        var parsed = JsonNode.Parse(json);
        Assert.NotNull(parsed);
        Assert.Contains("p(95)", json, StringComparison.Ordinal);
    }
}
