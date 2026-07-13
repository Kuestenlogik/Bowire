// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Monitoring;

/// <summary>
/// Runs a probe once and records the outcome (#102). The pipeline per run:
/// execute the recording → evaluate the assertions → append the outcome to the
/// ledger → detect a pass↔fail transition against the ledger's previous row →
/// fire every configured signaler on a transition. This is the single unit the
/// scheduler invokes on each tick.
/// </summary>
public sealed class ProbeRunner
{
    private readonly IProbeExecutor _executor;
    private readonly OutcomeLedger _ledger;
    private readonly IReadOnlyList<ISignaler> _signalers;
    private readonly TimeProvider _time;
    private readonly ILogger<ProbeRunner>? _log;
    private readonly Action<Probe, ProbeOutcome>? _onOutcome;

    public ProbeRunner(
        IProbeExecutor executor,
        OutcomeLedger ledger,
        IEnumerable<ISignaler> signalers,
        TimeProvider timeProvider,
        ILogger<ProbeRunner>? logger = null,
        Action<Probe, ProbeOutcome>? onOutcome = null)
    {
        ArgumentNullException.ThrowIfNull(signalers);
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _signalers = signalers.ToArray();
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = logger;
        _onOutcome = onOutcome;
    }

    /// <summary>
    /// Execute the probe once, persist the outcome, and signal on a transition.
    /// Returns the recorded outcome. Signaler failures are swallowed (logged) so
    /// one bad channel never aborts the run or blocks the ledger write.
    /// </summary>
    public async Task<ProbeOutcome> RunOnceAsync(Probe probe, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var previous = _ledger.LastOutcome(probe.Name)?.Result;
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();

        ProbeOutcome outcome;
        try
        {
            var exec = await _executor.ExecuteAsync(probe, ct).ConfigureAwait(false);
            var verdicts = probe.Assertions.Select(a => a.Evaluate(exec)).ToArray();
            var passed = verdicts.All(v => v.Passed);
            outcome = new ProbeOutcome
            {
                TimestampUnixMs = now,
                Result = passed ? ProbeResult.Pass : ProbeResult.Fail,
                LatencyMs = exec.LatencyMs,
                Assertions = verdicts,
            };
        }
        catch (ProbeExecutionException ex)
        {
            outcome = new ProbeOutcome
            {
                TimestampUnixMs = now,
                Result = ProbeResult.Error,
                Error = ex.Message,
            };
        }

        _ledger.Append(probe.Name, outcome);
        _onOutcome?.Invoke(probe, outcome); // per-run observer (e.g. the CLI's live line)

        var transition = TransitionDetector.Detect(previous, outcome.Result);
        if (transition != ProbeTransition.None)
        {
            await FireSignalersAsync(new SignalEvent(probe, transition, outcome), ct).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task FireSignalersAsync(SignalEvent signal, CancellationToken ct)
    {
        foreach (var signaler in _signalers)
        {
            try
            {
                await signaler.DeliverAsync(signal, ct).ConfigureAwait(false);
            }
            catch (SignalerException ex)
            {
                // One channel failing must not abort the run or the others.
                LogSignalerFailed(_log, signal.Probe.Name, signaler.GetType().Name, ex);
            }
        }
    }

    private static readonly Action<ILogger, string, string, Exception?> LogSignalerFailedMessage =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogSignalerFailed)),
            "Monitoring signaler {Signaler} failed for probe {Probe}");

    private static void LogSignalerFailed(ILogger? log, string probe, string signaler, Exception ex)
    {
        if (log is not null) LogSignalerFailedMessage(log, signaler, probe, ex);
    }
}

/// <summary>
/// Thrown by an <see cref="ISignaler"/> when delivery fails. The runner catches
/// this (and only this) so a bad channel is logged and skipped without aborting
/// the run — a general exception (a bug in the signaler) still propagates.
/// </summary>
public sealed class SignalerException : Exception
{
    public SignalerException(string message) : base(message) { }
    public SignalerException(string message, Exception innerException) : base(message, innerException) { }
    public SignalerException() { }
}
