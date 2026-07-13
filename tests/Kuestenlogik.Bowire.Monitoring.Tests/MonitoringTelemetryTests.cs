// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// Coverage for <see cref="MonitoringTelemetry"/> — the per-run duration + outcome
/// instruments record with the expected names and tags. Observed via a
/// <see cref="MeterListener"/>; no OpenTelemetry dependency needed.
/// </summary>
public sealed class MonitoringTelemetryTests
{
    private sealed record Measured(string Instrument, string Outcome, string Probe);

    private static string Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var t in tags)
        {
            if (t.Key == key) return t.Value?.ToString() ?? "";
        }
        return "";
    }

    [Fact]
    public void Record_emits_duration_and_outcome_with_probe_and_outcome_tags()
    {
        var seen = new List<Measured>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == MonitoringTelemetry.MeterName) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            seen.Add(new Measured(instrument.Name, Tag(tags, "outcome"), Tag(tags, "probe"))));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            seen.Add(new Measured(instrument.Name, Tag(tags, "outcome"), Tag(tags, "probe"))));
        listener.Start();

        MonitoringTelemetry.Record("payments", ProbeResult.Fail, 42.0);

        Assert.Contains(seen, m => m is { Instrument: "bowire.monitoring.probe.outcome", Outcome: "fail", Probe: "payments" });
        Assert.Contains(seen, m => m is { Instrument: "bowire.monitoring.probe.duration", Outcome: "fail", Probe: "payments" });
    }

    [Theory]
    [InlineData(ProbeResult.Pass, "pass")]
    [InlineData(ProbeResult.Fail, "fail")]
    [InlineData(ProbeResult.Error, "error")]
    public void Outcome_tag_maps_the_result(ProbeResult result, string expected)
    {
        var outcomes = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "bowire.monitoring.probe.outcome") l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => outcomes.Add(Tag(tags, "outcome")));
        listener.Start();

        MonitoringTelemetry.Record("p", result, 1.0);
        Assert.Contains(expected, outcomes, StringComparer.Ordinal);
    }
}
