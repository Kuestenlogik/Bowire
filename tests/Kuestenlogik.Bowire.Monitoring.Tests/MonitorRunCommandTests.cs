// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Kuestenlogik.Bowire.Monitoring.Cli;

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// Coverage for the <c>bowire monitor run</c> command surface — the <c>--once</c>
/// path exit codes + output, driven with a fake executor (via the
/// <see cref="MonitorRunCommand.ExecutorFactory"/> seam) and a temp ledger, so
/// no live target is needed.
/// </summary>
public sealed class MonitorRunCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly Func<IProbeExecutor> _prevFactory;

    public MonitorRunCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bowire-mon-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _prevFactory = MonitorRunCommand.ExecutorFactory;
    }

    public void Dispose()
    {
        MonitorRunCommand.ExecutorFactory = _prevFactory;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private sealed class FakeExecutor(int status) : IProbeExecutor
    {
        public Task<ProbeExecutionResult> ExecuteAsync(Probe probe, CancellationToken ct = default)
            => Task.FromResult(new ProbeExecutionResult(status, 5, """{"ok":true}"""));
    }

    private sealed class CancelingExecutor(CancellationTokenSource cts) : IProbeExecutor
    {
        public async Task<ProbeExecutionResult> ExecuteAsync(Probe probe, CancellationToken ct = default)
        {
            await cts.CancelAsync(); // end the monitoring loop after the first run
            return new ProbeExecutionResult(200, 5, "ok");
        }
    }

    private string WriteProbe(string name = "health")
    {
        var path = Path.Combine(_dir, name + ".probe.json");
        File.WriteAllText(path, $$"""
            {
              "name": "{{name}}",
              "schedule": "every 60s",
              "assertions": [ { "kind": "status", "expected": "200" } ],
              "recording": { "id": "r", "name": "{{name}}", "steps": [ { "id": "s1", "protocol": "rest", "service": "S", "method": "M" } ] }
            }
            """);
        return path;
    }

    private MonitorRunOptions Options(params string[] files) =>
        new() { ProbeFiles = files, LedgerRoot = _dir, Once = true };

    [Fact]
    public async Task RunOnce_passing_probe_exits_0_and_records_pass()
    {
        MonitorRunCommand.ExecutorFactory = () => new FakeExecutor(200);
        var probe = WriteProbe();
        using var outw = new StringWriter();
        using var errw = new StringWriter();

        var code = await MonitorRunCommand.RunAsync(Options(probe), outw, errw, TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Contains("Pass", outw.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_dir, "health.jsonl")));
    }

    [Fact]
    public async Task RunOnce_failing_probe_exits_2()
    {
        MonitorRunCommand.ExecutorFactory = () => new FakeExecutor(500);
        var probe = WriteProbe();
        using var outw = new StringWriter();
        using var errw = new StringWriter();

        var code = await MonitorRunCommand.RunAsync(Options(probe), outw, errw, TestContext.Current.CancellationToken);

        Assert.Equal(2, code);
        Assert.Contains("Fail", outw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_probe_file_is_skipped_and_exits_1()
    {
        using var outw = new StringWriter();
        using var errw = new StringWriter();

        var code = await MonitorRunCommand.RunAsync(
            Options(Path.Combine(_dir, "does-not-exist.probe.json")),
            outw, errw, TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("No valid probe files", errw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_probe_file_is_reported_and_skipped()
    {
        var bad = Path.Combine(_dir, "bad.probe.json");
        await File.WriteAllTextAsync(bad, "{ not json", TestContext.Current.CancellationToken);
        using var outw = new StringWriter();
        using var errw = new StringWriter();

        var code = await MonitorRunCommand.RunAsync(Options(bad), outw, errw, TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("Skipping", errw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loop_mode_runs_then_exits_cleanly_on_cancel()
    {
        using var cts = new CancellationTokenSource();
        MonitorRunCommand.ExecutorFactory = () => new CancelingExecutor(cts);
        var probe = WriteProbe();
        using var outw = new StringWriter();
        using var errw = new StringWriter();

        // Loop mode (not --once): the never-run probe fires immediately, the
        // executor cancels, and the next scheduled wait unwinds the loop.
        var code = await MonitorRunCommand.RunAsync(
            new MonitorRunOptions { ProbeFiles = [probe], LedgerRoot = _dir, Once = false },
            outw, errw, cts.Token);

        Assert.Equal(0, code);
        Assert.Contains("Monitoring 1 probe(s)", outw.ToString(), StringComparison.Ordinal);
        Assert.Contains("Stopped.", outw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_signal_scheme_is_reported_but_run_still_succeeds()
    {
        MonitorRunCommand.ExecutorFactory = () => new FakeExecutor(200);
        var probe = WriteProbe();
        using var outw = new StringWriter();
        using var errw = new StringWriter();

        // A valid scheme (resolves, but never fires — first --once run is the
        // baseline, no transition) + an unknown scheme (reported + skipped).
        var code = await MonitorRunCommand.RunAsync(
            new MonitorRunOptions
            {
                ProbeFiles = [probe],
                LedgerRoot = _dir,
                Once = true,
                Signals = ["pagerduty:rk", "teams:https://x"],
            },
            outw, errw, TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Contains("teams", errw.ToString(), StringComparison.Ordinal);
        Assert.Contains("Ignoring --signal", errw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Command_exposes_a_run_subcommand()
    {
        var cmd = new MonitorCliCommand().Build();
        Assert.Equal("monitor", cmd.Name);
        Assert.Contains(cmd.Subcommands, c => c.Name == "run");
    }

    [Fact]
    public async Task Invoking_run_through_the_parser_runs_end_to_end()
    {
        MonitorRunCommand.ExecutorFactory = () => new FakeExecutor(200);
        var probe = WriteProbe();
        using var outw = new StringWriter();

        var cmd = new MonitorCliCommand().Build();
        var invocationConfig = new InvocationConfiguration { Output = outw, Error = outw };
        var parse = cmd.Parse(["run", "--once", "--ledger-root", _dir, probe]);
        Assert.Empty(parse.Errors);

        var code = await parse.InvokeAsync(invocationConfig, TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.Contains("Pass", outw.ToString(), StringComparison.Ordinal);
    }
}
