// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// Coverage for <see cref="ProbeRunner"/> — the per-run pipeline (execute →
/// assert → append → detect transition → signal). Executor + signalers are
/// faked; the ledger is a real temp file so the resume/transition read path
/// runs end-to-end.
/// </summary>
public sealed class ProbeRunnerTests : IDisposable
{
    private readonly string _dir;
    private readonly OutcomeLedger _ledger;
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    public ProbeRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bowire-lh-run-" + Guid.NewGuid().ToString("N"));
        _ledger = new OutcomeLedger(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    // ---- fakes ----

    private sealed class FakeExecutor(ProbeExecutionResult? result = null, bool throwsError = false) : IProbeExecutor
    {
        public int Calls { get; private set; }
        public Task<ProbeExecutionResult> ExecuteAsync(Probe probe, CancellationToken ct = default)
        {
            Calls++;
            if (throwsError) throw new ProbeExecutionException("connection refused");
            return Task.FromResult(result ?? new ProbeExecutionResult(200, 20, "ok"));
        }
    }

    private sealed class RecordingSignaler(bool throws = false) : ISignaler
    {
        public List<SignalEvent> Events { get; } = [];
        public Task DeliverAsync(SignalEvent signal, CancellationToken ct = default)
        {
            Events.Add(signal);
            if (throws) throw new SignalerException("webhook 500");
            return Task.CompletedTask;
        }
    }

    private static Probe MakeProbe(params ProbeAssertion[] assertions)
    {
        Assert.True(ProbeSchedule.TryParse("every 60s", out var schedule, out _));
        return new Probe
        {
            Name = "payments",
            Schedule = schedule!,
            Recording = new BowireRecording { Id = "r", Name = "payments" },
            Assertions = assertions,
        };
    }

    private ProbeRunner MakeRunner(IProbeExecutor executor, params ISignaler[] signalers)
        => new(executor, _ledger, signalers, new FixedTimeProvider(Now));

    private static ProbeAssertion Status(int code) => new() { Kind = ProbeAssertionKind.Status, Expected = code.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    // ---- tests ----

    [Fact]
    public async Task Passing_run_is_recorded_as_pass()
    {
        var runner = MakeRunner(new FakeExecutor(new ProbeExecutionResult(200, 12, "ok")));
        var outcome = await runner.RunOnceAsync(MakeProbe(Status(200)), TestContext.Current.CancellationToken);

        Assert.Equal(ProbeResult.Pass, outcome.Result);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), outcome.TimestampUnixMs);
        Assert.Equal(ProbeResult.Pass, _ledger.LastOutcome("payments")!.Result);
    }

    [Fact]
    public async Task Failing_assertion_is_recorded_as_fail()
    {
        var runner = MakeRunner(new FakeExecutor(new ProbeExecutionResult(500, 12, "err")));
        var outcome = await runner.RunOnceAsync(MakeProbe(Status(200)), TestContext.Current.CancellationToken);
        Assert.Equal(ProbeResult.Fail, outcome.Result);
        Assert.Single(outcome.Assertions);
    }

    [Fact]
    public async Task Executor_error_is_recorded_as_error()
    {
        var runner = MakeRunner(new FakeExecutor(throwsError: true));
        var outcome = await runner.RunOnceAsync(MakeProbe(Status(200)), TestContext.Current.CancellationToken);
        Assert.Equal(ProbeResult.Error, outcome.Result);
        Assert.Equal("connection refused", outcome.Error);
    }

    [Fact]
    public async Task Transition_to_failing_fires_the_signaler()
    {
        // Seed a prior passing run so this failing run is a pass→fail edge.
        _ledger.Append("payments", new ProbeOutcome { TimestampUnixMs = 1, Result = ProbeResult.Pass });
        var signaler = new RecordingSignaler();
        var runner = MakeRunner(new FakeExecutor(new ProbeExecutionResult(500, 12, "err")), signaler);

        await runner.RunOnceAsync(MakeProbe(Status(200)), TestContext.Current.CancellationToken);

        var evt = Assert.Single(signaler.Events);
        Assert.Equal(ProbeTransition.ToFailing, evt.Transition);
        Assert.Equal("payments", evt.Probe.Name);
    }

    [Fact]
    public async Task Steady_state_does_not_fire_the_signaler()
    {
        _ledger.Append("payments", new ProbeOutcome { TimestampUnixMs = 1, Result = ProbeResult.Pass });
        var signaler = new RecordingSignaler();
        var runner = MakeRunner(new FakeExecutor(new ProbeExecutionResult(200, 12, "ok")), signaler);

        await runner.RunOnceAsync(MakeProbe(Status(200)), TestContext.Current.CancellationToken);
        Assert.Empty(signaler.Events);
    }

    [Fact]
    public async Task First_failing_run_fires_the_signaler()
    {
        var signaler = new RecordingSignaler();
        var runner = MakeRunner(new FakeExecutor(new ProbeExecutionResult(500, 12, "err")), signaler);
        await runner.RunOnceAsync(MakeProbe(Status(200)), TestContext.Current.CancellationToken);
        Assert.Equal(ProbeTransition.ToFailing, Assert.Single(signaler.Events).Transition);
    }

    [Fact]
    public async Task Signaler_failure_is_swallowed()
    {
        _ledger.Append("payments", new ProbeOutcome { TimestampUnixMs = 1, Result = ProbeResult.Pass });
        var bad = new RecordingSignaler(throws: true);
        var good = new RecordingSignaler();
        var runner = MakeRunner(new FakeExecutor(new ProbeExecutionResult(500, 12, "err")), bad, good);

        // The throwing signaler must not abort the run or block the next signaler.
        var outcome = await runner.RunOnceAsync(MakeProbe(Status(200)), TestContext.Current.CancellationToken);
        Assert.Equal(ProbeResult.Fail, outcome.Result);
        Assert.Single(good.Events);
    }

    [Fact]
    public async Task Runs_with_no_signalers_registered()
    {
        var runner = MakeRunner(new FakeExecutor(new ProbeExecutionResult(200, 12, "ok")));
        var outcome = await runner.RunOnceAsync(MakeProbe(Status(200)), TestContext.Current.CancellationToken);
        Assert.Equal(ProbeResult.Pass, outcome.Result);
    }
}
