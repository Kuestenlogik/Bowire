// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the OWASP suite roll-up plumbing: the ten-entry catalog,
/// tag → entry matching (including the API1 / API10 prefix disambiguation),
/// the per-entry status derivation, and the console summary writer.
/// </summary>
public sealed class OwaspApiSuiteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScanFinding Finding(string owaspApi, ScanFindingStatus status, string severity = "medium")
        => new()
        {
            Template = SyntheticTemplate.Build($"BWR-TEST-{owaspApi}-{status}", "test finding",
                cwe: null, owaspApi: owaspApi, severity: severity, cvss: 5.3, remediation: "fix it"),
            Status = status,
            Detail = "detail",
        };

    [Fact]
    public void Catalog_HasTenOrderedEntries()
    {
        Assert.Equal(10, OwaspApiCatalog.Entries.Count);
        Assert.Equal("API1:2023", OwaspApiCatalog.Entries[0].Id);
        Assert.Equal("API10:2023", OwaspApiCatalog.Entries[9].Id);
    }

    [Theory]
    [InlineData("API1-2023-BOLA", "API1:2023")]
    [InlineData("API10-2023-UNSAFE", "API10:2023")]
    [InlineData("API9-2023-GRAPHQL-INTROSPECTION", "API9:2023")]
    public void Match_MapsTagToEntry(string tag, string expectedId)
        => Assert.Equal(expectedId, OwaspApiCatalog.Match(tag)?.Id);

    [Fact]
    public void Match_Api1PrefixDoesNotSwallowApi10()
    {
        // The trailing dash in the prefix keeps API10 from matching API1.
        Assert.Equal("API1:2023", OwaspApiCatalog.Match("API1-2023-BOLA")?.Id);
        Assert.Equal("API10:2023", OwaspApiCatalog.Match("API10-2023-UNSAFE")?.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-owasp-tag")]
    public void Match_UnknownOrEmpty_ReturnsNull(string? tag)
        => Assert.Null(OwaspApiCatalog.Match(tag));

    [Fact]
    public void Rollup_DerivesPerEntryStatus()
    {
        var findings = new[]
        {
            Finding("API1-2023-BOLA", ScanFindingStatus.Vulnerable),
            Finding("API2-2023-BROKENAUTH", ScanFindingStatus.Safe),
            Finding("API3-2023-BOPLA", ScanFindingStatus.Skipped),   // skipped-only → NotCovered
            Finding("API4-2023-RESOURCE", ScanFindingStatus.Error),  // error-only → Error
        };

        var rollup = OwaspApiSuite.Rollup(findings).ToDictionary(r => r.Entry.Id, r => r.Status);

        Assert.Equal(OwaspEntryStatus.Vulnerable, rollup["API1:2023"]);
        Assert.Equal(OwaspEntryStatus.Safe, rollup["API2:2023"]);
        Assert.Equal(OwaspEntryStatus.NotCovered, rollup["API3:2023"]);
        Assert.Equal(OwaspEntryStatus.Error, rollup["API4:2023"]);
        Assert.Equal(OwaspEntryStatus.NotCovered, rollup["API5:2023"]); // no finding at all
    }

    [Fact]
    public void Rollup_VulnerableWinsOverSafeInSameEntry()
    {
        var findings = new[]
        {
            Finding("API9-2023-A", ScanFindingStatus.Safe),
            Finding("API9-2023-B", ScanFindingStatus.Vulnerable),
        };
        var api9 = OwaspApiSuite.Rollup(findings).Single(r => r.Entry.Id == "API9:2023");
        Assert.Equal(OwaspEntryStatus.Vulnerable, api9.Status);
        Assert.Equal(1, api9.VulnCount);
    }

    [Fact]
    public async Task WriteSummary_RendersTableAndHonestFooter()
    {
        var findings = new[] { Finding("API1-2023-BOLA", ScanFindingStatus.Vulnerable) };
        await using var sw = new StringWriter();

        await OwaspApiSuite.WriteSummaryAsync(findings, sw);

        var text = sw.ToString();
        Assert.Contains("OWASP API Security Top 10 (2023)", text, StringComparison.Ordinal);
        Assert.Contains("[VULN]", text, StringComparison.Ordinal);
        Assert.Contains("[----]", text, StringComparison.Ordinal); // uncovered entries present
        Assert.Contains("not a", text, StringComparison.OrdinalIgnoreCase); // honest-footer note
    }
}
