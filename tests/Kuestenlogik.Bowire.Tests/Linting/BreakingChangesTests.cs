// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Linting;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Tests.Linting;

/// <summary>
/// Breaking changes against a baseline, as lint findings (#583).
/// </summary>
/// <remarks>
/// The line between breaking and not is <c>bowire diff</c>'s, deliberately —
/// a second classification would mean two commands disagreeing about the same
/// change. What is pinned here is that each side of that line lands where it
/// should, and that the rules config reaches this rule like any other.
/// </remarks>
public sealed class BreakingChangesTests
{
    private static readonly BowireServiceInfo Baseline = Svc(
        Method("GetOrder", Msg("Req", Field("id"))),
        Method("ListOrders", Msg("Req")),
        Method("CancelOrder", Msg("Req", Field("id"))));

    [Fact]
    public void A_Removed_Method_Breaks()
    {
        var current = Svc(Method("GetOrder", Msg("Req", Field("id"))), Method("ListOrders", Msg("Req")));
        var finding = Assert.Single(BowireBreakingChanges.Find([Baseline], [current]));

        Assert.Equal(BowireBreakingChanges.RuleId, finding.RuleId);
        Assert.Equal(BowireLintSeverity.High, finding.Severity);
        Assert.Contains("CancelOrder", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Changed_Request_Shape_Breaks()
    {
        var current = Svc(
            Method("GetOrder", Msg("Req", Field("order_id"))),
            Method("ListOrders", Msg("Req")),
            Method("CancelOrder", Msg("Req", Field("id"))));
        var finding = Assert.Single(BowireBreakingChanges.Find([Baseline], [current]));
        Assert.Contains("signature", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Removed_Service_Is_One_Finding_For_The_Service()
    {
        var findings = BowireBreakingChanges.Find([Baseline], []);
        var finding = Assert.Single(findings);
        Assert.Null(finding.Method);
        Assert.Contains("removed", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Additions_And_Deprecations_Do_Not_Break()
    {
        var current = Svc(
            Method("GetOrder", Msg("Req", Field("id"))),
            Method("ListOrders", Msg("Req")) with { Deprecated = true },
            Method("CancelOrder", Msg("Req", Field("id"))),
            Method("RefundOrder", Msg("Req")));
        Assert.Empty(BowireBreakingChanges.Find([Baseline], [current]));
    }

    [Fact]
    public void The_Rules_Config_Can_Switch_It_Off_And_Regrade_It()
    {
        var current = Svc(Method("GetOrder", Msg("Req", Field("id"))));

        var off = BowireLintConfig.Parse(
            $$"""{ "rules": { "{{BowireBreakingChanges.RuleId}}": { "enabled": false } } }""");
        Assert.Empty(BowireBreakingChanges.Find([Baseline], [current], off));

        var medium = BowireLintConfig.Parse(
            $$"""{ "rules": { "{{BowireBreakingChanges.RuleId}}": { "severity": "medium" } } }""");
        Assert.All(BowireBreakingChanges.Find([Baseline], [current], medium),
            f => Assert.Equal(BowireLintSeverity.Medium, f.Severity));
    }

    [Fact]
    public void It_Is_Never_Picked_Up_As_An_Ordinary_Rule()
    {
        // Rule discovery instantiates every rule type it can construct. As an
        // ordinary rule this one would have no baseline, never fire, and still
        // show up — a JUnit case that passes forever, claiming a check it cannot
        // make.
        Assert.DoesNotContain(BowireSchemaLinter.DiscoverRules(), r => r.Id == BowireBreakingChanges.RuleId);
    }

    private static BowireServiceInfo Svc(params BowireMethodInfo[] methods) => new("orders.v1.OrderService", "orders.v1", [.. methods]);

    private static BowireMethodInfo Method(string name, BowireMessageInfo input)
        => new(name, "orders.v1.OrderService/" + name, false, false, input, Msg("Res"), "Unary");

    private static BowireMessageInfo Msg(string name, params BowireFieldInfo[] fields) => new(name, name, [.. fields]);

    private static BowireFieldInfo Field(string name) => new(name, 1, "string", "optional", false, false, null, null);
}
