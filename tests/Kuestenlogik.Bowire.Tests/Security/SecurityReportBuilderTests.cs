// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the deterministic security-report builder (#107): SARIF →
/// severity/OWASP grouping, markdown skeleton, and diff-vs-baseline.
/// </summary>
public sealed class SecurityReportBuilderTests
{
    // A SARIF run with the given results; rules carry owaspApi + security-severity
    // the way `bowire scan --out` emits them. Built via object graph + serializer
    // so there's no JSON-brace escaping to get wrong.
    private static string Sarif(params (string RuleId, string Name, string Owasp, string Cvss, string Level, string Fp)[] rows)
    {
        var doc = new
        {
            runs = new[]
            {
                new
                {
                    tool = new { driver = new { rules = rows.Select(r => new
                    {
                        id = r.RuleId,
                        name = r.Name,
                        properties = new Dictionary<string, string> { ["owaspApi"] = r.Owasp, ["security-severity"] = r.Cvss },
                    }).ToArray() } },
                    results = rows.Select(r => new
                    {
                        ruleId = r.RuleId,
                        level = r.Level,
                        message = new { text = "m" },
                        partialFingerprints = new Dictionary<string, string> { ["bowire/v1"] = r.Fp },
                        locations = new[] { new { logicalLocations = new[] { new { fullyQualifiedName = "svc." + r.RuleId } } } },
                    }).ToArray(),
                },
            },
        };
        return JsonSerializer.Serialize(doc);
    }

    [Fact]
    public void Build_GroupsBySeverityAndOwasp()
    {
        var sarif = Sarif(
            ("R1", "BOLA on getOrder", "API1-2023-BOLA", "7.5", "error", "fp1"),
            ("R2", "CORS reflects origin", "API8-2023-SECMISCONF", "5.3", "warning", "fp2"));
        var report = SecurityReportBuilder.Build(sarif, target: "api.example.com");

        Assert.Equal(2, report.Findings.Count);
        Assert.Equal(1, report.BySeverity["high"]);
        Assert.Equal(1, report.BySeverity["medium"]);
        Assert.Equal(1, report.ByOwasp["API1-2023-BOLA"]);
        var bola = report.Findings.Single(f => f.RuleId == "R1");
        Assert.Equal("BOLA on getOrder", bola.Title);
        Assert.Equal("high", bola.Severity);
        Assert.Equal(7.5, bola.Cvss);
        Assert.Equal("svc.R1", bola.Location);
    }

    [Fact]
    public void ToMarkdown_ContainsHeaderTotalsAndFindings()
    {
        var sarif = Sarif(
            ("R1", "BOLA on getOrder", "API1-2023-BOLA", "7.5", "error", "fp1"),
            ("R2", "CORS reflects origin", "API8-2023-SECMISCONF", "5.3", "warning", "fp2"));
        var md = SecurityReportBuilder.Build(sarif, target: "api.example.com").ToMarkdown();

        Assert.Contains("# Security scan report — api.example.com", md, StringComparison.Ordinal);
        Assert.Contains("1 high, 1 medium", md, StringComparison.Ordinal);
        Assert.Contains("By OWASP API Top 10", md, StringComparison.Ordinal);
        Assert.Contains("### [high] BOLA on getOrder", md, StringComparison.Ordinal);
        Assert.Contains("**CVSS**: 7.5", md, StringComparison.Ordinal);
    }

    [Fact]
    public void ToMarkdown_WithExecutiveSummary_IncludesIt()
    {
        var sarif = Sarif(("R1", "X", "API1-2023-BOLA", "7.5", "error", "fp1"));
        var md = SecurityReportBuilder.Build(sarif).ToMarkdown(executiveSummary: "The API leaks orders across tenants.");
        Assert.Contains("## Executive summary", md, StringComparison.Ordinal);
        Assert.Contains("leaks orders across tenants", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Diff_ClassifiesNewFixedPersisting()
    {
        var current = Sarif(
            ("R1", "BOLA", "API1-2023-BOLA", "7.5", "error", "fp1"),      // persisting
            ("R3", "New injection", "API3-2023", "8.1", "error", "fp3")); // new
        var baseline = Sarif(
            ("R1", "BOLA", "API1-2023-BOLA", "7.5", "error", "fp1"),      // persisting
            ("R2", "Old CORS", "API8-2023-SECMISCONF", "5.3", "warning", "fp2")); // fixed

        var report = SecurityReportBuilder.Build(current, baseline);
        Assert.NotNull(report.Diff);
        Assert.Equal("R3", Assert.Single(report.Diff!.New).RuleId);
        Assert.Equal("R2", Assert.Single(report.Diff.Fixed).RuleId);
        Assert.Equal("R1", Assert.Single(report.Diff.Persisting).RuleId);

        var md = report.ToMarkdown();
        Assert.Contains("**Since baseline:** 1 new, 1 fixed, 1 still open.", md, StringComparison.Ordinal);
        Assert.Contains("🆕 New", md, StringComparison.Ordinal);
        Assert.Contains("✅ Fixed", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NoFindings_RendersEmpty()
    {
        var md = SecurityReportBuilder.Build("""{"runs":[{"tool":{"driver":{"rules":[]}},"results":[]}]}""").ToMarkdown();
        Assert.Contains("**Findings:** none", md, StringComparison.Ordinal);
        Assert.Contains("No findings.", md, StringComparison.Ordinal);
    }
}
