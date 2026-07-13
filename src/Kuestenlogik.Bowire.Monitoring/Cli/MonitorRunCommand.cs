// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Kuestenlogik.Bowire;

namespace Kuestenlogik.Bowire.Monitoring.Cli;

/// <summary>Options for <c>bowire monitor run</c>.</summary>
public sealed record MonitorRunOptions
{
    /// <summary>Probe definition files to load + run.</summary>
    public IReadOnlyList<string> ProbeFiles { get; init; } = [];

    /// <summary>Outcome-ledger root; null → the default <c>~/.bowire/monitoring</c>.</summary>
    public string? LedgerRoot { get; init; }

    /// <summary>Run each probe once and exit, rather than looping on the schedule.</summary>
    public bool Once { get; init; }

    /// <summary>
    /// Outbound signal channels, each a <c>&lt;scheme&gt;:&lt;arg&gt;</c> spec
    /// (<c>slack:&lt;webhook&gt;</c>, <c>pagerduty:&lt;routing-key&gt;</c>, …).
    /// Resolved through the installed signaler packages; an unknown scheme is
    /// reported and skipped. The console channel is always on.
    /// </summary>
    public IReadOnlyList<string> Signals { get; init; } = [];
}

/// <summary>
/// The <c>bowire monitor run</c> implementation, split out of the CLI command
/// so it is testable with a pinned <see cref="TextWriter"/> and a swapped
/// executor. Loads the probe files, wires the recording-replay executor to the
/// installed protocol plugins, and either runs each probe once (<c>--once</c>)
/// or loops on the schedule until cancelled.
/// </summary>
public static class MonitorRunCommand
{
    /// <summary>
    /// Test seam — builds the <see cref="IProbeExecutor"/>. Production replays
    /// recordings through the installed plugins (<see cref="BowireProtocolRegistry"/>
    /// discovery); tests swap in a fake so no live target is needed.
    /// </summary>
    internal static Func<IProbeExecutor> ExecutorFactory { get; set; } =
        () => new RecordingProbeExecutor(BowireProtocolRegistry.Discover());

    /// <summary>Run the command. Returns a process exit code.</summary>
    public static async Task<int> RunAsync(
        MonitorRunOptions options, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var probes = new List<Probe>();
        foreach (var path in options.ProbeFiles)
        {
            try
            {
                probes.Add(ProbeFile.Load(path));
            }
            catch (ProbeFileException ex)
            {
                await error.WriteLineAsync($"Skipping '{path}': {ex.Message}").ConfigureAwait(false);
            }
        }

        if (probes.Count == 0)
        {
            await error.WriteLineAsync("No valid probe files to monitor.").ConfigureAwait(false);
            return 1;
        }

        var ledger = new OutcomeLedger(string.IsNullOrWhiteSpace(options.LedgerRoot)
            ? MonitoringServiceCollectionExtensions.DefaultLedgerRoot()
            : options.LedgerRoot);

        // The console channel is always on; the network channels are resolved
        // through the installed opt-in signaler packages. An unknown scheme is
        // reported and skipped, never fatal.
        var signalers = new List<ISignaler> { new ConsoleSignaler(output) };
        if (options.Signals.Count > 0)
        {
            var registry = SignalerRegistry.Discover();
            foreach (var spec in options.Signals)
            {
                var signaler = registry.Resolve(spec, out var signalError);
                if (signaler is not null)
                {
                    signalers.Add(signaler);
                }
                else
                {
                    await error.WriteLineAsync($"Ignoring --signal: {signalError}").ConfigureAwait(false);
                }
            }
        }

        var runner = new ProbeRunner(
            ExecutorFactory(), ledger, signalers, TimeProvider.System, onOutcome: (p, oc) => WriteOutcomeLine(output, p, oc));

        if (options.Once)
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Running {probes.Count} probe(s) once.")).ConfigureAwait(false);
            var anyNotPassing = false;
            foreach (var probe in probes)
            {
                var outcome = await runner.RunOnceAsync(probe, ct).ConfigureAwait(false);
                if (outcome.Result != ProbeResult.Pass) anyNotPassing = true;
            }
            return anyNotPassing ? 2 : 0;
        }

        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Monitoring {probes.Count} probe(s). Press Ctrl+C to stop.")).ConfigureAwait(false);
        var scheduler = new TimeProviderProbeScheduler(runner, ledger, TimeProvider.System);
        try
        {
            await scheduler.RunAsync(probes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C / shutdown — the normal exit path.
        }
        await output.WriteLineAsync("Stopped.").ConfigureAwait(false);
        return 0;
    }

    private static void WriteOutcomeLine(TextWriter output, Probe probe, ProbeOutcome outcome)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(outcome.TimestampUnixMs)
            .ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var detail = outcome.Result == ProbeResult.Error
            ? outcome.Error ?? "error"
            : string.Create(CultureInfo.InvariantCulture, $"{outcome.LatencyMs:0}ms");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[{time}] {probe.Name}: {outcome.Result} ({detail})"));
    }
}
