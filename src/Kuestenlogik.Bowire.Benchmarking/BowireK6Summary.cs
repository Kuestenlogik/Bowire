// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuestenlogik.Bowire.Benchmarking;

/// <summary>
/// Renders a run as k6-summary JSON (#234 shape, #360 thresholds).
/// <para>
/// The metric block mirrors what the workbench rail already exports
/// (<c>http_req_duration</c> / <c>http_reqs</c> / <c>iterations</c> /
/// <c>iteration_duration</c> / <c>http_req_failed</c> / <c>checks</c>), so a
/// dashboard that ingests a run exported from the UI ingests one written by
/// <c>bowire bench run</c> without a second code path.
/// </para>
/// <para>
/// Thresholds ride along the way k6 itself reports them: keyed by their
/// source text inside the metric they constrain, each with an <c>ok</c>
/// flag. That is what makes "downstream tooling agrees" true rather than
/// aspirational — a CI dashboard reading k6 summaries finds Bowire's
/// budgets exactly where it looks for k6's.
/// </para>
/// </summary>
public static class BowireK6Summary
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>
    /// Build the summary document for <paramref name="stats"/>, annotating
    /// each metric with any threshold that constrains it.
    /// </summary>
    public static JsonObject Build(BowireBenchmarkStats stats, IReadOnlyList<BowireThresholdResult>? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(stats);

        var total = stats.Count + stats.Errors;
        var rate = stats.Throughput;

        var duration = Trend(stats);
        var iterationDuration = Trend(stats);

        var httpReqs = new JsonObject
        {
            ["type"] = "counter",
            ["contains"] = "default",
            ["values"] = new JsonObject { ["count"] = total, ["rate"] = rate },
        };

        var iterations = new JsonObject
        {
            ["type"] = "counter",
            ["contains"] = "default",
            ["values"] = new JsonObject { ["count"] = total, ["rate"] = rate },
        };

        // k6's http_req_failed counts failures as 'passes' of the failure
        // rate — the rail's export already follows that inversion, so the
        // CLI keeps it rather than quietly disagreeing.
        var httpReqFailed = new JsonObject
        {
            ["type"] = "rate",
            ["contains"] = "default",
            ["values"] = new JsonObject
            {
                ["rate"] = stats.ErrorRate,
                ["passes"] = stats.Errors,
                ["fails"] = stats.Count,
            },
        };

        var checks = new JsonObject
        {
            ["type"] = "rate",
            ["contains"] = "default",
            ["values"] = new JsonObject
            {
                ["rate"] = total > 0 ? (double)stats.Count / total : 0,
                ["passes"] = stats.Count,
                ["fails"] = stats.Errors,
            },
        };

        var metrics = new JsonObject
        {
            ["http_req_duration"] = duration,
            ["http_reqs"] = httpReqs,
            ["iterations"] = iterations,
            ["iteration_duration"] = iterationDuration,
            ["http_req_failed"] = httpReqFailed,
            ["checks"] = checks,
        };

        AttachThresholds(metrics, thresholds);

        return new JsonObject { ["metrics"] = metrics };
    }

    /// <summary>Serialise <see cref="Build"/>'s document.</summary>
    public static string Render(BowireBenchmarkStats stats, IReadOnlyList<BowireThresholdResult>? thresholds = null)
        => Build(stats, thresholds).ToJsonString(WriteOpts);

    private static JsonObject Trend(BowireBenchmarkStats stats) => new()
    {
        ["type"] = "trend",
        ["contains"] = "time",
        ["values"] = new JsonObject
        {
            ["avg"] = stats.Avg,
            ["min"] = stats.Min,
            ["max"] = stats.Max,
            ["med"] = stats.P50,
            ["p(90)"] = stats.P90,
            ["p(95)"] = stats.P95,
            ["p(99)"] = stats.P99,
            ["count"] = stats.Count,
        },
    };

    /// <summary>
    /// Hang each threshold off the k6 metric it constrains. Latency and
    /// error-rate budgets belong to the metrics k6 names for them;
    /// throughput has no dedicated k6 metric, so it rides on
    /// <c>http_reqs</c>, whose <c>rate</c> value is that number.
    /// </summary>
    private static void AttachThresholds(JsonObject metrics, IReadOnlyList<BowireThresholdResult>? thresholds)
    {
        if (thresholds is null || thresholds.Count == 0) return;

        foreach (var result in thresholds)
        {
            var metricName = result.Threshold.Metric switch
            {
                BowireBenchmarkMetric.ErrorRate => "http_req_failed",
                BowireBenchmarkMetric.Throughput => "http_reqs",
                _ => "http_req_duration",
            };
            if (metrics[metricName] is not JsonObject metric) continue;

            if (metric["thresholds"] is not JsonObject bucket)
            {
                bucket = [];
                metric["thresholds"] = bucket;
            }
            bucket[result.Threshold.ToString()] = new JsonObject { ["ok"] = result.Ok };
        }
    }
}
