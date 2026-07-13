// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Monitoring;

/// <summary>
/// Fires when a probe crosses the pass↔fail line (#102). Implementations are
/// the outbound integrations — Slack / PagerDuty / OTLP-logs — each shipping as
/// a separate opt-in package and registered only when the operator passes the
/// matching <c>--signal</c> flag. Core Monitoring registers <b>no</b> signaler,
/// so nothing leaves the host by default.
/// </summary>
public interface ISignaler
{
    /// <summary>Deliver a transition to this channel. Failures must not abort the run.</summary>
    Task DeliverAsync(SignalEvent signal, CancellationToken ct = default);
}

/// <summary>
/// The payload a signaler receives — which probe, what happened, and the
/// outcome that triggered it.
/// </summary>
public sealed record SignalEvent(
    Probe Probe,
    ProbeTransition Transition,
    ProbeOutcome Outcome);
