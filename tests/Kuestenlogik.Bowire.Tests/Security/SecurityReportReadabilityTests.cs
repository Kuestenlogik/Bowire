// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Security;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// The scan report as a reader meets it in a PR comment. A finding used
/// to read "[high] BWR-BUILTIN-TLS-001 · OWASP: API8-2023-SECMISCONF ·
/// Location: .github/workflows/dogfood-pr-report.yml" — the rule's id
/// as its own title, a label where the risk's name belongs, and the
/// Code Scanning placeholder path where the scan target belongs. These
/// pin the three: the check's name is the heading, the OWASP code is
/// named and linked, the location is the target (or a file the reader
/// can open) and the remediation is on the page.
/// </summary>
[Collection("CwdSerialised")]
public sealed class SecurityReportReadabilityTests : IDisposable
{
    private static readonly string[] s_env = ["GITHUB_SERVER_URL", "GITHUB_REPOSITORY", "GITHUB_SHA"];
    private readonly Dictionary<string, string?> _original = s_env.ToDictionary(v => v, Environment.GetEnvironmentVariable);

    public void Dispose()
    {
        foreach (var (k, v) in _original) Environment.SetEnvironmentVariable(k, v);
    }

    [Fact]
    public void The_Sarif_rule_carries_the_checks_name_and_remediation()
    {
        var finding = new ScanFinding
        {
            Template = SyntheticTemplate.PlaintextHttp(),
            Status = ScanFindingStatus.Vulnerable,
            Detail = "http://",
        };
        var rule = Assert.Single(ScanCommand.ExtractRules([finding]));
        Assert.Equal("BWR-BUILTIN-TLS-001", rule.Id);
        Assert.Equal("Target serves plaintext http://", rule.Name);
        Assert.Equal("Target serves plaintext http://", rule.ShortDescription.Text);
        Assert.StartsWith("Enforce https://", rule.FullDescription.Text, StringComparison.Ordinal);
    }

    private static string DastSarif(string ruleName, string target, string placeholderPath = ".github/workflows/dogfood-pr-report.yml") =>
        JsonSerializer.Serialize(new
        {
            runs = new[]
            {
                new
                {
                    tool = new { driver = new { rules = new[] { new
                    {
                        id = "BWR-BUILTIN-TLS-001",
                        name = ruleName,
                        shortDescription = new { text = ruleName },
                        fullDescription = new { text = "Enforce https:// at the load balancer / ingress." },
                        helpUri = "https://cwe.mitre.org/data/definitions/319.html",
                        properties = new Dictionary<string, string> { ["owaspApi"] = "API8-2023-SECMISCONF", ["security-severity"] = "7.4" },
                    } } } },
                    results = new[] { new
                    {
                        ruleId = "BWR-BUILTIN-TLS-001",
                        level = "error",
                        message = new { text = $"Target serves plaintext http:// (target: {target})" },
                        locations = new[] { new
                        {
                            physicalLocation = new { artifactLocation = new { uri = placeholderPath } },
                            logicalLocations = new[] { new { fullyQualifiedName = target } },
                        } },
                    } },
                },
            },
        });

    [Fact]
    public void A_dast_finding_names_the_target_not_the_workflow_that_ran_the_scan()
    {
        var md = SecurityReportBuilder.Build(DastSarif("Target serves plaintext http://", "http://127.0.0.1:6000")).ToMarkdown();

        Assert.Contains("# Security scan report — http://127.0.0.1:6000", md, StringComparison.Ordinal);
        Assert.Contains("### [high] Target serves plaintext http://", md, StringComparison.Ordinal);
        Assert.Contains("**Target**: <http://127.0.0.1:6000>", md, StringComparison.Ordinal);
        Assert.DoesNotContain("dogfood-pr-report.yml", md, StringComparison.Ordinal);
        Assert.Contains("**What to do**: Enforce https://", md, StringComparison.Ordinal);
        Assert.Contains("**Reference**: <https://cwe.mitre.org/data/definitions/319.html>", md, StringComparison.Ordinal);
    }

    [Fact]
    public void The_owasp_code_is_named_and_linked_in_the_table_and_on_the_finding()
    {
        var md = SecurityReportBuilder.Build(DastSarif("Target serves plaintext http://", "http://127.0.0.1:6000")).ToMarkdown();

        Assert.Contains("| Entry | Risk | Findings |", md, StringComparison.Ordinal);
        Assert.Contains("| [API8-2023-SECMISCONF](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) | Security Misconfiguration | 1 |", md, StringComparison.Ordinal);
        Assert.Contains("**OWASP**: [API8-2023-SECMISCONF](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) — Security Misconfiguration", md, StringComparison.Ordinal);
        Assert.Equal("Unsafe Consumption of APIs", SecurityReport.OwaspName("API10-2023-UNSAFE"));
        Assert.Contains("0xaa-unsafe-consumption-of-apis", SecurityReport.OwaspLink("API10-2023-UNSAFE"), StringComparison.Ordinal);
        Assert.Equal("CWE-79", SecurityReport.OwaspName("CWE-79"));
        Assert.Equal("CWE-79", SecurityReport.OwaspLink("CWE-79"));
    }

    [Fact]
    public void A_rule_named_after_its_id_falls_back_to_the_results_message()
    {
        // Older SARIF, where the rule's name was its id: the message still
        // says what the check found.
        var md = SecurityReportBuilder.Build(DastSarif("BWR-BUILTIN-TLS-001", "http://127.0.0.1:6000")).ToMarkdown();
        Assert.Contains("### [high] Target serves plaintext http:// (target: http://127.0.0.1:6000)", md, StringComparison.Ordinal);
        Assert.DoesNotContain("### [high] BWR-BUILTIN-TLS-001", md, StringComparison.Ordinal);
        Assert.Contains("**Target**: <http://127.0.0.1:6000>", md, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_location_links_into_the_repository_under_actions_and_stays_text_elsewhere()
    {
        var sarif = JsonSerializer.Serialize(new
        {
            runs = new[]
            {
                new
                {
                    tool = new { driver = new { rules = new[] { new { id = "T1", name = "Secret in template" } } } },
                    results = new[] { new
                    {
                        ruleId = "T1",
                        level = "warning",
                        message = new { text = "m" },
                        locations = new[] { new { physicalLocation = new { artifactLocation = new { uri = "templates/api/orders.yaml" } } } },
                    } },
                },
            },
        });

        Environment.SetEnvironmentVariable("GITHUB_SERVER_URL", null);
        Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", null);
        Environment.SetEnvironmentVariable("GITHUB_SHA", null);
        Assert.Contains("**Location**: templates/api/orders.yaml", SecurityReportBuilder.Build(sarif).ToMarkdown(), StringComparison.Ordinal);

        Environment.SetEnvironmentVariable("GITHUB_SERVER_URL", "https://github.com");
        Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", "Kuestenlogik/Bowire");
        Environment.SetEnvironmentVariable("GITHUB_SHA", "abc123");
        Assert.Contains("**Location**: [templates/api/orders.yaml](https://github.com/Kuestenlogik/Bowire/blob/abc123/templates/api/orders.yaml)",
            SecurityReportBuilder.Build(sarif).ToMarkdown(), StringComparison.Ordinal);
    }
}
