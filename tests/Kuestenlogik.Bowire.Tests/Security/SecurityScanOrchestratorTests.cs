// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the deterministic AI-scan orchestration control flow (#104):
/// threat-model rank → risk-threshold + cap → per-endpoint probe → triage
/// keep/suppress → report. The AI/scanner steps are faked so the composition
/// logic is tested on its own.
/// </summary>
public sealed class SecurityScanOrchestratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OrchestratorEndpoint Ep(string id) => new(id, "/" + id, "GET");

    [Fact]
    public async Task Chains_ThreatModel_Threshold_Probe_Triage_Report()
    {
        var steps = new FakeSteps
        {
            Risk = { ["e1"] = 8, ["e2"] = 3, ["e3"] = 6 },
            Findings =
            {
                ["e1"] = [Finding("e1", "R1"), Finding("e1", "R2")],
                ["e3"] = [Finding("e3", "R3")],
            },
            TriageScore = { ["R1"] = 80, ["R2"] = 20, ["R3"] = 90 },
            Report = "REPORT",
        };

        var result = await SecurityScanOrchestrator.RunAsync(
            [Ep("e1"), Ep("e2"), Ep("e3")],
            new SecurityScanOrchestrationOptions { RiskThreshold = 5, TriageKeepThreshold = 50 },
            steps, Ct);

        // e2 (risk 3) is below threshold → not probed; probed order is risk-desc.
        Assert.Equal(["e1", "e3"], result.Probed.Select(p => p.Endpoint.EndpointId));
        Assert.DoesNotContain("e2", steps.Probed);
        // R2 (score 20) suppressed; R1 + R3 kept with their triage score.
        Assert.Equal(["R1", "R3"], result.Findings.Select(f => f.RuleId).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(1, result.SuppressedCount);
        Assert.All(result.Findings, f => Assert.True(f.Triaged));
        Assert.Equal(80, result.Findings.Single(f => f.RuleId == "R1").RealScore);
        Assert.Equal("REPORT", result.ReportMarkdown);
    }

    [Fact]
    public async Task MaxEndpoints_CapsProbedSet()
    {
        var steps = new FakeSteps { Risk = { ["a"] = 9, ["b"] = 8, ["c"] = 7, ["d"] = 6 } };
        var result = await SecurityScanOrchestrator.RunAsync(
            [Ep("a"), Ep("b"), Ep("c"), Ep("d")],
            new SecurityScanOrchestrationOptions { RiskThreshold = 5, MaxEndpoints = 2 },
            steps, Ct);

        Assert.Equal(["a", "b"], result.Probed.Select(p => p.Endpoint.EndpointId));
        Assert.Equal(2, steps.Probed.Count);
    }

    [Fact]
    public async Task GenerateReportFalse_SkipsReportStep()
    {
        var steps = new FakeSteps { Risk = { ["e1"] = 8 }, Findings = { ["e1"] = [Finding("e1", "R1")] }, TriageScore = { ["R1"] = 90 }, Report = "REPORT" };
        var result = await SecurityScanOrchestrator.RunAsync(
            [Ep("e1")], new SecurityScanOrchestrationOptions { GenerateReport = false }, steps, Ct);

        Assert.Null(result.ReportMarkdown);
        Assert.False(steps.ReportCalled);
    }

    [Fact]
    public async Task NoEndpointsAboveThreshold_ProbesNothing()
    {
        var steps = new FakeSteps { Risk = { ["e1"] = 2, ["e2"] = 1 } };
        var result = await SecurityScanOrchestrator.RunAsync(
            [Ep("e1"), Ep("e2")], new SecurityScanOrchestrationOptions { RiskThreshold = 5 }, steps, Ct);

        Assert.Empty(result.Probed);
        Assert.Empty(result.Findings);
        Assert.Empty(steps.Probed);
    }

    [Fact]
    public async Task Trace_RecordsEachStage()
    {
        var steps = new FakeSteps { Risk = { ["e1"] = 8 }, Findings = { ["e1"] = [Finding("e1", "R1")] }, TriageScore = { ["R1"] = 90 }, Report = "R" };
        var result = await SecurityScanOrchestrator.RunAsync([Ep("e1")], new SecurityScanOrchestrationOptions(), steps, Ct);

        Assert.Contains(result.Trace, t => t.Contains("threat-model", StringComparison.Ordinal));
        Assert.Contains(result.Trace, t => t.Contains("triage", StringComparison.Ordinal));
        Assert.Contains(result.Trace, t => t.Contains("report", StringComparison.Ordinal));
    }

    private static OrchestratedFinding Finding(string endpointId, string ruleId)
        => new(endpointId, ruleId, "Title " + ruleId, "high", "API1-2023-BOLA");

    private sealed class FakeSteps : ISecurityScanSteps
    {
        public Dictionary<string, int> Risk { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, OrchestratedFinding[]> Findings { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> TriageScore { get; } = new(StringComparer.Ordinal);
        public string? Report { get; init; }
        public List<string> Probed { get; } = [];
        public bool ReportCalled { get; private set; }

        public Task<IReadOnlyList<RankedEndpoint>> ThreatModelAsync(IReadOnlyList<OrchestratorEndpoint> endpoints, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RankedEndpoint>>(
                endpoints.Select(e => new RankedEndpoint(e, Risk.GetValueOrDefault(e.EndpointId, 0), "r")).ToArray());

        public Task<IReadOnlyList<OrchestratedFinding>> ProbeAsync(RankedEndpoint endpoint, CancellationToken ct)
        {
            Probed.Add(endpoint.Endpoint.EndpointId);
            return Task.FromResult<IReadOnlyList<OrchestratedFinding>>(
                Findings.GetValueOrDefault(endpoint.Endpoint.EndpointId, []));
        }

        public Task<(int RealScore, string? Reasoning)> TriageAsync(OrchestratedFinding finding, CancellationToken ct)
            => Task.FromResult((TriageScore.GetValueOrDefault(finding.RuleId, 0), (string?)"reason"));

        public Task<string?> ReportAsync(OrchestratedScanResult interim, CancellationToken ct)
        {
            ReportCalled = true;
            return Task.FromResult(Report);
        }
    }
}
