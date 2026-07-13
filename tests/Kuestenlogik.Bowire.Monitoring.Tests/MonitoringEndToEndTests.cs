// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire;

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// End-to-end: a probe file is parsed, its recording is actually replayed
/// through a protocol plugin, the assertions are evaluated, the outcome is
/// written to the ledger, and a pass↔fail transition fires the signaler. Walks
/// the whole engine — parse → execute → assert → append → detect → signal —
/// against a stubbed plugin so no live target is needed.
/// </summary>
public sealed class MonitoringEndToEndTests : IDisposable
{
    private readonly string _dir;
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    public MonitoringEndToEndTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bowire-lh-e2e-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private const string ProbeJson = """
        {
          "name": "payments-health",
          "schedule": "every 60s",
          "severity": "crit",
          "assertions": [ { "kind": "status", "expected": "200" } ],
          "recording": {
            "id": "rec_1",
            "name": "payments health",
            "steps": [ { "id": "s1", "protocol": "rest", "service": "root", "method": "GET /health", "body": "{}" } ]
          }
        }
        """;

    private sealed class RecordingSignaler : ISignaler
    {
        public List<ProbeTransition> Transitions { get; } = [];
        public Task DeliverAsync(SignalEvent signal, CancellationToken ct = default)
        {
            Transitions.Add(signal.Transition);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Full_pipeline_pass_then_fail_then_recover()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = ProbeFile.Parse(ProbeJson);

        // A mutable status lets us drive the health check up and down.
        var status = "200";
        var registry = new BowireProtocolRegistry();
        registry.Register(new StubProtocol("rest",
            () => new InvokeResult("""{"health":"ok"}""", 12, status, new Dictionary<string, string>())));

        var ledger = new OutcomeLedger(_dir);
        var signaler = new RecordingSignaler();
        var runner = new ProbeRunner(new RecordingProbeExecutor(registry), ledger, [signaler], new FixedTimeProvider(Now));

        // 1) Healthy — passes, no transition (first pass is the baseline).
        var r1 = await runner.RunOnceAsync(probe, ct);
        Assert.Equal(ProbeResult.Pass, r1.Result);
        Assert.Empty(signaler.Transitions);

        // 2) The service starts returning 500 — fails, fires ToFailing.
        status = "500";
        var r2 = await runner.RunOnceAsync(probe, ct);
        Assert.Equal(ProbeResult.Fail, r2.Result);
        Assert.Equal(ProbeTransition.ToFailing, signaler.Transitions[^1]);

        // 3) Recovery — passes again, fires ToPassing.
        status = "200";
        var r3 = await runner.RunOnceAsync(probe, ct);
        Assert.Equal(ProbeResult.Pass, r3.Result);
        Assert.Equal(ProbeTransition.ToPassing, signaler.Transitions[^1]);

        // The ledger holds all three runs; the last is the current state.
        var lines = await File.ReadAllLinesAsync(ledger.PathFor("payments-health"), ct);
        Assert.Equal(3, lines.Length);
        Assert.Equal(ProbeResult.Pass, ledger.LastOutcome("payments-health")!.Result);
    }

    [Fact]
    public async Task Missing_plugin_records_an_error_outcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = ProbeFile.Parse(ProbeJson);
        var ledger = new OutcomeLedger(_dir);
        // Empty registry → the recording's 'rest' plugin isn't loaded.
        var runner = new ProbeRunner(new RecordingProbeExecutor(new BowireProtocolRegistry()), ledger, [], new FixedTimeProvider(Now));

        var outcome = await runner.RunOnceAsync(probe, ct);
        Assert.Equal(ProbeResult.Error, outcome.Result);
        Assert.Contains("rest", outcome.Error!, StringComparison.Ordinal);
    }
}
