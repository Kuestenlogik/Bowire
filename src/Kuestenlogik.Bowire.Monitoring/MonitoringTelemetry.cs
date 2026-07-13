// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Kuestenlogik.Bowire.Monitoring;

/// <summary>
/// The monitoring metrics surface (#102) — a single <see cref="Meter"/> with the
/// two instruments the design calls for, in the shared <c>bowire.*</c> namespace
/// so #29's Grafana dashboards gain a section rather than a parallel one:
/// <list type="bullet">
///   <item><c>bowire.monitoring.probe.duration</c> — histogram of run latency (ms).</item>
///   <item><c>bowire.monitoring.probe.outcome</c> — counter, tags: probe + outcome.</item>
/// </list>
/// Recording is dependency-free (<see cref="System.Diagnostics.Metrics"/> is in
/// the BCL) and cheap when no listener is attached, so the engine records on
/// every run. The OTLP <b>export</b> lives in the opt-in
/// <c>Kuestenlogik.Bowire.Monitoring.Otlp</c> package; without it the instruments
/// still record in-process (readable by the workbench surface).
/// </summary>
public static class MonitoringTelemetry
{
    /// <summary>Meter name the OTLP exporter subscribes to via <c>AddMeter</c>.</summary>
    public const string MeterName = "bowire.monitoring";

    private static readonly string Version =
        typeof(MonitoringTelemetry).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>The shared meter all monitoring metrics flow through.</summary>
    public static readonly Meter Meter = new(MeterName, Version);

    /// <summary>Per-run latency, milliseconds.</summary>
    public static readonly Histogram<double> ProbeDuration =
        Meter.CreateHistogram<double>("bowire.monitoring.probe.duration", unit: "ms",
            description: "Latency of a probe run.");

    /// <summary>Per-run outcome counter, tagged with the probe name + outcome.</summary>
    public static readonly Counter<long> ProbeOutcome =
        Meter.CreateCounter<long>("bowire.monitoring.probe.outcome",
            description: "Probe runs, tagged by probe name and outcome (pass / fail / error).");

    /// <summary>Record one run's duration + outcome. No-op cost when unobserved.</summary>
    public static void Record(string probeName, ProbeResult result, double latencyMs)
    {
        var outcome = result switch
        {
            ProbeResult.Pass => "pass",
            ProbeResult.Fail => "fail",
            _ => "error",
        };
        var tags = new TagList
        {
            { "probe", probeName },
            { "outcome", outcome },
        };
        ProbeOutcome.Add(1, tags);
        ProbeDuration.Record(latencyMs, tags);
    }
}
