// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
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

    /// <summary>
    /// A probe name no other test can emit.
    /// </summary>
    /// <remarks>
    /// Both the meter and <see cref="MeterListener"/> are process-global, so a
    /// listener here also sees measurements from every other test class in this
    /// assembly — <c>ProbeRunner</c> records one per probe it runs, and those
    /// classes run in parallel with this one. Without a name only this test can
    /// produce, a foreign measurement could satisfy the assertions and the test
    /// would pass while the code under test emitted nothing at all.
    /// </remarks>
    private static string UniqueProbeName([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        => $"{caller}-{Guid.NewGuid():N}";

    [Fact]
    public void Record_emits_duration_and_outcome_with_probe_and_outcome_tags()
    {
        // Concurrent, not List: the callback fires on whichever thread recorded
        // the measurement, so a parallel test class appending here while the
        // assertions below enumerate is exactly the "Collection was modified"
        // failure this replaces.
        var seen = new ConcurrentQueue<Measured>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == MonitoringTelemetry.MeterName) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            seen.Enqueue(new Measured(instrument.Name, Tag(tags, "outcome"), Tag(tags, "probe"))));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            seen.Enqueue(new Measured(instrument.Name, Tag(tags, "outcome"), Tag(tags, "probe"))));
        listener.Start();

        var probe = UniqueProbeName();
        MonitoringTelemetry.Record(probe, ProbeResult.Fail, 42.0);

        // Snapshot once. Asserting straight against the queue would enumerate a
        // collection other threads are still writing to.
        var measured = seen.ToArray();
        Assert.Contains(measured, m => m is { Instrument: "bowire.monitoring.probe.outcome", Outcome: "fail" } && m.Probe == probe);
        Assert.Contains(measured, m => m is { Instrument: "bowire.monitoring.probe.duration", Outcome: "fail" } && m.Probe == probe);
    }

    [Theory]
    [InlineData(ProbeResult.Pass, "pass")]
    [InlineData(ProbeResult.Fail, "fail")]
    [InlineData(ProbeResult.Error, "error")]
    public void Outcome_tag_maps_the_result(ProbeResult result, string expected)
    {
        // Same two hazards as above, and the second one bites harder here: a
        // parallel ProbeRunner emitting "pass" would satisfy a bare
        // Assert.Contains even if the mapping under test were broken. Keying on
        // a probe name only this case can produce is what makes the assertion
        // about this call rather than about the assembly.
        var outcomes = new ConcurrentQueue<Measured>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "bowire.monitoring.probe.outcome") l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            outcomes.Enqueue(new Measured(instrument.Name, Tag(tags, "outcome"), Tag(tags, "probe"))));
        listener.Start();

        var probe = UniqueProbeName();
        MonitoringTelemetry.Record(probe, result, 1.0);

        var measured = outcomes.ToArray();
        Assert.Contains(measured, m => m.Probe == probe && m.Outcome == expected);
    }
}
