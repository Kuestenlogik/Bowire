// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Kuestenlogik.Bowire.Monitoring;

/// <summary>
/// The always-on Core signaler — writes a prominent line to a
/// <see cref="TextWriter"/> when a probe crosses the pass↔fail line. This is
/// the default channel the CLI registers; the network signalers (Slack /
/// PagerDuty / OTLP) are opt-in sibling packages layered on top.
/// </summary>
public sealed class ConsoleSignaler : ISignaler
{
    private readonly TextWriter _out;

    public ConsoleSignaler(TextWriter output)
        => _out = output ?? throw new ArgumentNullException(nameof(output));

    /// <inheritdoc/>
    public async Task DeliverAsync(SignalEvent signal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var mark = signal.Transition == ProbeTransition.ToFailing ? "!! ALERT" : "== RECOVERED";
        await _out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"  {mark}  {signal.Probe.Name} [{signal.Probe.Severity}] -> {signal.Outcome.Result}")).ConfigureAwait(false);
    }
}
