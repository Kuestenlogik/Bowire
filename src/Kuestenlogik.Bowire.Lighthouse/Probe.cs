// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Lighthouse;

/// <summary>
/// A Lighthouse probe (#102) — a saved invocation (<see cref="BowireRecording"/>)
/// plus the three extras that turn it into a scheduled health check: a
/// <see cref="Schedule"/>, the <see cref="Assertions"/> that must pass, and a
/// <see cref="Severity"/> that routes the signal. This is the sentence the
/// whole engine serves: "a probe is a recording with a schedule, assertions,
/// and a severity."
/// </summary>
public sealed class Probe
{
    /// <summary>Stable, filesystem-safe name — also the ledger filename stem.</summary>
    public required string Name { get; init; }

    /// <summary>When the probe runs. See <see cref="ProbeSchedule"/>.</summary>
    public required ProbeSchedule Schedule { get; init; }

    /// <summary>The saved invocation the probe replays each run.</summary>
    public required BowireRecording Recording { get; init; }

    /// <summary>Must-pass predicates over the probe's response.</summary>
    public IReadOnlyList<ProbeAssertion> Assertions { get; init; } = [];

    /// <summary>Routes the signal to the signaler's per-channel severity mapping.</summary>
    public ProbeSeverity Severity { get; init; } = ProbeSeverity.Warn;
}

/// <summary>Severity of a probe's failure — maps onto the signaler's routing.</summary>
public enum ProbeSeverity
{
    /// <summary>Informational — surfaced but not paged.</summary>
    Info = 0,
    /// <summary>Warning — the default.</summary>
    Warn = 1,
    /// <summary>Critical — page on-call.</summary>
    Crit = 2,
}

/// <summary>Outcome of a single probe run.</summary>
public enum ProbeResult
{
    /// <summary>Every assertion passed.</summary>
    Pass = 0,
    /// <summary>The probe ran but at least one assertion failed.</summary>
    Fail = 1,
    /// <summary>The probe couldn't run (transport error, timeout, …).</summary>
    Error = 2,
}
