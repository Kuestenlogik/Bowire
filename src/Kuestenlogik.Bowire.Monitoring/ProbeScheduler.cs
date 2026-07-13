// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Monitoring;

/// <summary>
/// Drives probe runs on their schedule (#102, Decision 1). The default
/// implementation is <see cref="TimeProviderProbeScheduler"/> — a hand-rolled
/// <see cref="TimeProvider"/> loop, no Quartz. A Quartz-grade scheduler arrives
/// as an optional sibling package contributing this interface.
/// </summary>
public interface IProbeScheduler
{
    /// <summary>
    /// Run every probe on its own cadence until <paramref name="ct"/> cancels.
    /// Each probe's first run is anchored to its last ledger row (lazy-start
    /// resume), not the wall-clock at startup.
    /// </summary>
    Task RunAsync(IEnumerable<Probe> probes, CancellationToken ct);
}

/// <summary>
/// The Core scheduler: one <see cref="TimeProvider"/>-backed loop per probe.
/// Building on <see cref="TimeProvider"/> (not <c>DateTime.UtcNow</c> +
/// <c>Task.Delay()</c>) makes cadence, drift, and lazy-start resume drivable
/// from a fake clock in tests. Each iteration reads the probe's last ledger row,
/// computes the delay to its next run via <see cref="ProbeSchedule"/>, waits,
/// then runs it once through the <see cref="ProbeRunner"/>.
/// </summary>
public sealed class TimeProviderProbeScheduler : IProbeScheduler
{
    private readonly ProbeRunner _runner;
    private readonly OutcomeLedger _ledger;
    private readonly TimeProvider _time;

    public TimeProviderProbeScheduler(ProbeRunner runner, OutcomeLedger ledger, TimeProvider timeProvider)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc/>
    public Task RunAsync(IEnumerable<Probe> probes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(probes);
        return Task.WhenAll(probes.Select(p => RunProbeLoopAsync(p, ct)));
    }

    /// <summary>
    /// The per-probe loop: wait until the next scheduled run, run once, repeat.
    /// Exits cleanly on cancellation. Exposed for tests that drive a single probe
    /// against a fake clock.
    /// </summary>
    public async Task RunProbeLoopAsync(Probe probe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(probe);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var last = _ledger.LastOutcome(probe.Name);
                DateTimeOffset? lastRun = last is null
                    ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(last.TimestampUnixMs);

                var delay = probe.Schedule.DelayUntilNext(lastRun, _time.GetUtcNow());
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                }

                await _runner.RunOnceAsync(probe, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path — the loop was cancelled.
        }
    }
}
