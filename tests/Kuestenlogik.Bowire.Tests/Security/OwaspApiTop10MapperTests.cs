// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the deterministic OWASP API Top 10 per-method mapper (#106):
/// the rule-based tri-state status + suggested probe for each entry.
/// </summary>
public sealed class OwaspApiTop10MapperTests
{
    private static OwaspPanelRow Row(IReadOnlyList<OwaspPanelRow> rows, string entry)
        => rows.Single(r => r.Entry == entry);

    [Fact]
    public void Map_ReturnsAllTenEntries()
    {
        var rows = OwaspApiTop10Mapper.Map(new OwaspMethodDescriptor("/health", "GET"));
        Assert.Equal(10, rows.Count);
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.Title)));
    }

    [Fact]
    public void IdInPath_FlagsApi1Bola()
    {
        var rows = OwaspApiTop10Mapper.Map(new OwaspMethodDescriptor("/orders/{id}", "GET"));
        var api1 = Row(rows, "API1:2023");
        Assert.Equal(OwaspRiskStatus.AtRisk, api1.Status);
        Assert.NotNull(api1.SuggestedProbe);
    }

    [Fact]
    public void WriteWithFields_FlagsApi3MassAssignment()
    {
        var rows = OwaspApiTop10Mapper.Map(new OwaspMethodDescriptor("/users", "POST", ["name", "email"]));
        Assert.Equal(OwaspRiskStatus.AtRisk, Row(rows, "API3:2023").Status);
    }

    [Fact]
    public void ReadOnly_NoApi3()
    {
        var rows = OwaspApiTop10Mapper.Map(new OwaspMethodDescriptor("/users", "GET"));
        Assert.Equal(OwaspRiskStatus.NotApplicable, Row(rows, "API3:2023").Status);
    }

    [Fact]
    public void AdminPath_FlagsApi5Bfla()
    {
        var rows = OwaspApiTop10Mapper.Map(new OwaspMethodDescriptor("/admin/reset", "POST"));
        Assert.Equal(OwaspRiskStatus.AtRisk, Row(rows, "API5:2023").Status);
    }

    [Fact]
    public void AuthPath_FlagsApi2Maybe()
    {
        var rows = OwaspApiTop10Mapper.Map(new OwaspMethodDescriptor("/auth/login", "POST"));
        Assert.Equal(OwaspRiskStatus.Maybe, Row(rows, "API2:2023").Status);
    }

    [Fact]
    public void UrlField_FlagsApi7SsrfAndApi10()
    {
        var rows = OwaspApiTop10Mapper.Map(new OwaspMethodDescriptor("/fetch", "POST", ["callbackUrl"]));
        Assert.Equal(OwaspRiskStatus.AtRisk, Row(rows, "API7:2023").Status);
        Assert.Equal(OwaspRiskStatus.Maybe, Row(rows, "API10:2023").Status);
    }

    [Fact]
    public void Api4AndApi8_AlwaysMaybe_Api6AndApi9_AlwaysNa()
    {
        var rows = OwaspApiTop10Mapper.Map(new OwaspMethodDescriptor("/health", "GET"));
        Assert.Equal(OwaspRiskStatus.Maybe, Row(rows, "API4:2023").Status);
        Assert.Equal(OwaspRiskStatus.Maybe, Row(rows, "API8:2023").Status);
        Assert.Equal(OwaspRiskStatus.NotApplicable, Row(rows, "API6:2023").Status);
        Assert.Equal(OwaspRiskStatus.NotApplicable, Row(rows, "API9:2023").Status);
    }

    [Fact]
    public void BenignRead_NoAtRiskExceptNone()
    {
        var rows = OwaspApiTop10Mapper.Map(new OwaspMethodDescriptor("/health", "GET"));
        Assert.DoesNotContain(rows, r => r.Status == OwaspRiskStatus.AtRisk);
    }
}
