// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// Coverage for <see cref="TimeProviderProbeScheduler"/> and the DI wiring. The
/// loop test drives a never-run probe (next run is immediate, no timer wait) and
/// cancels from inside the executor so the loop runs once and exits cleanly.
/// </summary>
public sealed class ProbeSchedulerTests : IDisposable
{
    private readonly string _dir;
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    public ProbeSchedulerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bowire-lh-sch-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private sealed class CancelingExecutor(CancellationTokenSource cts) : IProbeExecutor
    {
        public int Calls { get; private set; }
        public async Task<ProbeExecutionResult> ExecuteAsync(Probe probe, CancellationToken ct = default)
        {
            Calls++;
            await cts.CancelAsync(); // end the loop after this run
            return new ProbeExecutionResult(200, 5, "ok");
        }
    }

    private static Probe NeverRunProbe()
    {
        Assert.True(ProbeSchedule.TryParse("every 60s", out var s, out _));
        return new Probe
        {
            Name = "p",
            Schedule = s!,
            Recording = new BowireRecording { Id = "r", Name = "p" },
        };
    }

    [Fact]
    public async Task Loop_runs_the_probe_once_then_exits_on_cancel()
    {
        using var cts = new CancellationTokenSource();
        var ledger = new OutcomeLedger(_dir);
        var executor = new CancelingExecutor(cts);
        var runner = new ProbeRunner(executor, ledger, [], new FixedTimeProvider(Now));
        var scheduler = new TimeProviderProbeScheduler(runner, ledger, new FixedTimeProvider(Now));

        await scheduler.RunProbeLoopAsync(NeverRunProbe(), cts.Token);

        Assert.Equal(1, executor.Calls);
        Assert.NotNull(ledger.LastOutcome("p"));
    }

    [Fact]
    public async Task RunAsync_over_an_empty_probe_set_completes()
    {
        using var cts = new CancellationTokenSource();
        var ledger = new OutcomeLedger(_dir);
        var runner = new ProbeRunner(new CancelingExecutor(cts), ledger, [], new FixedTimeProvider(Now));
        var scheduler = new TimeProviderProbeScheduler(runner, ledger, new FixedTimeProvider(Now));
        await scheduler.RunAsync([], TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AddBowireMonitoring_registers_the_engine()
    {
        using var cts = new CancellationTokenSource();
        var services = new ServiceCollection();
        services.AddSingleton<IProbeExecutor>(new CancelingExecutor(cts));
        services.AddBowireMonitoring(_dir);

        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<OutcomeLedger>());
        Assert.NotNull(sp.GetRequiredService<ProbeRunner>());
        Assert.IsType<TimeProviderProbeScheduler>(sp.GetRequiredService<IProbeScheduler>());
    }
}
