// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// Coverage for <see cref="ConsoleSignaler"/> — the always-on channel that
/// writes an alert / recovery line to a <see cref="TextWriter"/> on a transition.
/// </summary>
public sealed class ConsoleSignalerTests
{
    private static Probe MakeProbe(ProbeSeverity severity)
    {
        Assert.True(ProbeSchedule.TryParse("every 60s", out var s, out _));
        return new Probe { Name = "payments", Schedule = s!, Severity = severity, Recording = new BowireRecording { Id = "r", Name = "payments" } };
    }

    private static SignalEvent Event(ProbeTransition transition, ProbeResult result, ProbeSeverity severity = ProbeSeverity.Crit)
        => new(MakeProbe(severity), transition, new ProbeOutcome { TimestampUnixMs = 1, Result = result });

    [Fact]
    public async Task Failing_transition_writes_an_alert_line()
    {
        using var sw = new StringWriter();
        await new ConsoleSignaler(sw).DeliverAsync(Event(ProbeTransition.ToFailing, ProbeResult.Fail), TestContext.Current.CancellationToken);

        var line = sw.ToString();
        Assert.Contains("ALERT", line, StringComparison.Ordinal);
        Assert.Contains("payments", line, StringComparison.Ordinal);
        Assert.Contains("Crit", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_transition_writes_a_recovered_line()
    {
        using var sw = new StringWriter();
        await new ConsoleSignaler(sw).DeliverAsync(Event(ProbeTransition.ToPassing, ProbeResult.Pass), TestContext.Current.CancellationToken);
        Assert.Contains("RECOVERED", sw.ToString(), StringComparison.Ordinal);
    }
}
