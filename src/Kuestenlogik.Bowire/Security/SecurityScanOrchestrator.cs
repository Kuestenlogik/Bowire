// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security;

/// <summary>An endpoint the orchestrated scan can consider.</summary>
public sealed record OrchestratorEndpoint(string EndpointId, string Path, string? Method = null);

/// <summary>An endpoint ranked by the threat-model step.</summary>
public sealed record RankedEndpoint(OrchestratorEndpoint Endpoint, int RiskScore, string? Reason);

/// <summary>A finding produced against one endpoint, carrying its triage verdict once scored.</summary>
public sealed record OrchestratedFinding(
    string EndpointId, string RuleId, string Title, string Severity, string? OwaspApi,
    int RealScore = -1, string? TriageReasoning = null)
{
    /// <summary>Whether triage has scored this finding (RealScore &gt;= 0).</summary>
    public bool Triaged => RealScore >= 0;
}

/// <summary>Result of an orchestrated security scan.</summary>
public sealed record OrchestratedScanResult(
    IReadOnlyList<RankedEndpoint> Ranked,
    IReadOnlyList<RankedEndpoint> Probed,
    IReadOnlyList<OrchestratedFinding> Findings,
    int SuppressedCount,
    string? ReportMarkdown,
    IReadOnlyList<string> Trace);

/// <summary>Knobs for <see cref="SecurityScanOrchestrator"/>.</summary>
public sealed record SecurityScanOrchestrationOptions
{
    /// <summary>Only endpoints whose threat-model risk is at or above this run their probe stage. Default 5.</summary>
    public int RiskThreshold { get; init; } = 5;

    /// <summary>Cap on how many above-threshold endpoints to probe (highest-risk first). Default 10.</summary>
    public int MaxEndpoints { get; init; } = 10;

    /// <summary>Triage real-score at or above this keeps a finding; below it is suppressed as a likely false positive. Default 50.</summary>
    public int TriageKeepThreshold { get; init; } = 50;

    /// <summary>Whether to run the final report step. Default true.</summary>
    public bool GenerateReport { get; init; } = true;
}

/// <summary>
/// The step primitives the orchestrator chains — supplied by the caller so the
/// control flow stays deterministic + testable while the real work (AI
/// threat-model / probe execution / AI triage / AI report) is pluggable.
/// </summary>
public interface ISecurityScanSteps
{
    /// <summary>#59 — rank the endpoints by attack-surface risk.</summary>
    Task<IReadOnlyList<RankedEndpoint>> ThreatModelAsync(IReadOnlyList<OrchestratorEndpoint> endpoints, CancellationToken ct);

    /// <summary>#60/#62 + execution — suggest and run probes against one endpoint, returning raw findings.</summary>
    Task<IReadOnlyList<OrchestratedFinding>> ProbeAsync(RankedEndpoint endpoint, CancellationToken ct);

    /// <summary>#61 — score a finding real-vs-false-positive (0–100) with a one-line reason.</summary>
    Task<(int RealScore, string? Reasoning)> TriageAsync(OrchestratedFinding finding, CancellationToken ct);

    /// <summary>#107 — write the final report markdown from the kept findings. May return null.</summary>
    Task<string?> ReportAsync(OrchestratedScanResult interim, CancellationToken ct);
}

/// <summary>
/// AI security scan orchestration (#104): the single control flow that composes
/// the four primitives into a loop — threat-model → (per above-threshold
/// endpoint) probe → triage → report. Deterministic; the AI/scanner work is
/// injected via <see cref="ISecurityScanSteps"/>. This is the "story" the
/// individual primitives couldn't tell on their own.
/// </summary>
public static class SecurityScanOrchestrator
{
    public static async Task<OrchestratedScanResult> RunAsync(
        IReadOnlyList<OrchestratorEndpoint> endpoints,
        SecurityScanOrchestrationOptions options,
        ISecurityScanSteps steps,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(steps);

        var trace = new List<string>();

        // 1. Threat-model rank.
        var ranked = (await steps.ThreatModelAsync(endpoints, ct).ConfigureAwait(false) ?? [])
            .OrderByDescending(r => r.RiskScore).ToArray();
        trace.Add($"threat-model: ranked {ranked.Length} endpoint(s)");

        // 2. Select the above-threshold endpoints, highest-risk first, capped.
        var probed = ranked
            .Where(r => r.RiskScore >= options.RiskThreshold)
            .Take(Math.Max(0, options.MaxEndpoints))
            .ToArray();
        trace.Add($"selected {probed.Length} endpoint(s) at/above risk {options.RiskThreshold} (cap {options.MaxEndpoints})");

        // 3. Probe each selected endpoint, then 4. triage every finding.
        var kept = new List<OrchestratedFinding>();
        var suppressed = 0;
        foreach (var endpoint in probed)
        {
            ct.ThrowIfCancellationRequested();
            var raw = await steps.ProbeAsync(endpoint, ct).ConfigureAwait(false) ?? [];
            trace.Add($"probe {endpoint.Endpoint.Path}: {raw.Count} raw finding(s)");
            foreach (var finding in raw)
            {
                var (score, reasoning) = await steps.TriageAsync(finding, ct).ConfigureAwait(false);
                var scored = finding with { RealScore = Math.Clamp(score, 0, 100), TriageReasoning = reasoning };
                if (scored.RealScore >= options.TriageKeepThreshold) kept.Add(scored);
                else suppressed++;
            }
        }
        trace.Add($"triage: kept {kept.Count}, suppressed {suppressed} likely false-positive(s)");

        var result = new OrchestratedScanResult(ranked, probed, kept, suppressed, ReportMarkdown: null, trace);

        // 5. Report.
        if (options.GenerateReport)
        {
            var report = await steps.ReportAsync(result, ct).ConfigureAwait(false);
            trace.Add(report is null ? "report: skipped (no report produced)" : "report: generated");
            result = result with { ReportMarkdown = report };
        }

        return result;
    }
}
