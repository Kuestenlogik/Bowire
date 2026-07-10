// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security;

/// <summary>
/// Seam that executes probes against one endpoint for the AI scan orchestration
/// (#104). The scan engine (<c>Kuestenlogik.Bowire.Security.Scanner</c>) or the
/// host registers an implementation; the AI orchestration adapter resolves it
/// optionally, so the AI planning stages (threat-model → triage → report) work
/// even when no live probe executor is wired (plan-only mode) — and the core /
/// AI packages don't take a hard dependency on the scan engine.
/// </summary>
public interface ISecurityScanProbeRunner
{
    /// <summary>
    /// Run the probe stage against <paramref name="endpoint"/> on
    /// <paramref name="target"/> and return the raw (un-triaged) findings.
    /// </summary>
    Task<IReadOnlyList<OrchestratedFinding>> RunAsync(OrchestratorEndpoint endpoint, string target, CancellationToken ct);
}
