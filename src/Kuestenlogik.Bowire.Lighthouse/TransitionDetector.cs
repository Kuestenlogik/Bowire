// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Lighthouse;

/// <summary>
/// Decides whether a probe crossed the pass↔fail line (#102). A signal fires
/// only on a transition, never on every run, so an outage pages once and a
/// recovery clears once. The "previous" state is read from the ledger's last
/// row (Decision 2), so transition state survives a restart — a restart
/// mid-outage neither re-fires "went critical" nor drops "recovered".
/// </summary>
public static class TransitionDetector
{
    /// <summary>
    /// Compare the previous outcome (from the ledger, or <c>null</c> for a
    /// never-run probe) with the current one. <see cref="ProbeResult.Error"/> is
    /// treated as not-passing, so an error after a pass is a "went failing"
    /// transition and a pass after an error is a "recovered" transition.
    /// </summary>
    public static ProbeTransition Detect(ProbeResult? previous, ProbeResult current)
    {
        var wasPassing = previous == ProbeResult.Pass;
        var isPassing = current == ProbeResult.Pass;

        if (previous is null)
        {
            // First-ever run: a failure is a fresh "went failing" edge worth a
            // signal; a first pass is the healthy baseline, no signal.
            return isPassing ? ProbeTransition.None : ProbeTransition.ToFailing;
        }

        if (wasPassing && !isPassing) return ProbeTransition.ToFailing;
        if (!wasPassing && isPassing) return ProbeTransition.ToPassing;
        return ProbeTransition.None;
    }
}

/// <summary>The pass↔fail edge a run produced.</summary>
public enum ProbeTransition
{
    /// <summary>No edge — steady state (pass→pass or fail→fail).</summary>
    None = 0,
    /// <summary>Crossed from passing to failing/erroring — fire the alert.</summary>
    ToFailing = 1,
    /// <summary>Crossed from failing/erroring back to passing — fire the recovery.</summary>
    ToPassing = 2,
}
