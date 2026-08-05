// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for <see cref="MockConfigApplier.ApplyToStubs"/> (#561) — the
/// runtime apply path that clones the baseline stubs, applies per-field
/// overrides onto the clones, and compiles per-method conditional rules into
/// higher-priority match stubs.
/// </summary>
public sealed class MockConfigApplierRuleTests
{
    private static BowireRecordingStep RestStep(string service, string method, string response) => new()
    {
        Id = "base_" + method,
        Protocol = "rest",
        Service = service,
        Method = method,
        MethodType = "Unary",
        HttpVerb = "GET",
        HttpPath = "/" + method,
        Status = "OK",
        Response = response,
    };

    private static MockConfiguration ConfigWithRule(string? service, string? method, MockRulePredicate when, object response)
    {
        var config = new MockConfiguration();
        config.ConditionalRules.Add(new MockConditionalRule
        {
            Service = service,
            Method = method,
            When = when,
            Response = JsonSerializer.SerializeToElement(response),
        });
        return config;
    }

    [Fact]
    public void ApplyToStubs_Does_Not_Mutate_Baseline()
    {
        var baseline = new[] { RestStep("Orders", "list", """{"status":"pending"}""") };
        var config = new MockConfiguration();
        config.FieldOverrides.Add(new MockFieldOverride
        {
            Service = "Orders",
            Method = "list",
            JsonPath = "$.status",
            Value = JsonSerializer.SerializeToElement("shipped"),
        });

        var result = MockConfigApplier.ApplyToStubs(baseline, config);

        // The clone carries the override; the baseline step is untouched.
        using var cloneBody = JsonDocument.Parse(result[0].Response!);
        Assert.Equal("shipped", cloneBody.RootElement.GetProperty("status").GetString());
        Assert.Equal("""{"status":"pending"}""", baseline[0].Response);
    }

    [Fact]
    public void ApplyToStubs_Compiles_Conditional_Rule_Into_Higher_Priority_Match_Stub()
    {
        var baseline = new[] { RestStep("Orders", "list", """{"status":"pending"}""") };
        var config = ConfigWithRule("Orders", "list",
            new MockRulePredicate { JsonPath = "$.role", EqualTo = "admin" },
            new { status = "admin-view" });

        var result = MockConfigApplier.ApplyToStubs(baseline, config);

        Assert.Equal(2, result.Count); // base clone + rule stub
        var rule = result[1];
        // Rides the base stub's route.
        Assert.Equal("GET", rule.HttpVerb);
        Assert.Equal("/list", rule.HttpPath);
        // Higher-priority body predicate mirroring the rule's `when`.
        Assert.NotNull(rule.Match);
        Assert.Equal(10, rule.Match!.Priority);
        var predicate = Assert.Single(rule.Match.Body!);
        Assert.Equal("$.role", predicate.JsonPath);
        Assert.Equal("admin", predicate.EqualTo);
        // Serves the rule's variant.
        using var body = JsonDocument.Parse(rule.Response!);
        Assert.Equal("admin-view", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void ApplyToStubs_Wildcard_Rule_Fans_Out_To_Every_Matching_Stub()
    {
        // A `*`/`*` rule must attach to ALL routes (like field overrides), not
        // just the first — one rule stub per base stub.
        var baseline = new[]
        {
            RestStep("Orders", "list", """{"v":1}"""),
            RestStep("Users", "get", """{"v":1}"""),
        };
        var config = ConfigWithRule(null, "*",
            new MockRulePredicate { JsonPath = "$.x", Contains = "y" },
            new { v = 2 });

        var result = MockConfigApplier.ApplyToStubs(baseline, config);

        // 2 base clones + 2 rule stubs (one per route).
        Assert.Equal(4, result.Count);
        var rulePaths = result.Where(s => s.Match is not null).Select(s => s.HttpPath).ToList();
        Assert.Equal(2, rulePaths.Count);
        Assert.Contains("/get", rulePaths);
        Assert.Contains("/list", rulePaths);
        // The `contains` op is copied onto the compiled predicate (not just equals).
        Assert.All(result.Where(s => s.Match is not null), s => Assert.Equal("y", s.Match!.Body![0].Contains));
    }

    [Fact]
    public void ApplyToStubs_Matches_Op_Is_Copied_Onto_The_Compiled_Predicate()
    {
        var baseline = new[] { RestStep("Orders", "list", """{"v":1}""") };
        var config = ConfigWithRule("Orders", "list",
            new MockRulePredicate { JsonPath = "$.name", Matches = "^a.*z$" },
            new { v = 2 });

        var result = MockConfigApplier.ApplyToStubs(baseline, config);

        var rule = Assert.Single(result, s => s.Match is not null);
        Assert.Equal("^a.*z$", rule.Match!.Body![0].Matches);
    }

    [Theory]
    [InlineData(null, null, null)]      // when:{} — no path, no op
    [InlineData(null, null, "")]        // empty contains, no path
    public void ApplyToStubs_Empty_Predicate_Rule_Is_Skipped(string? jsonPath, string? equals, string? contains)
    {
        // A predicate that would match every request must not compile into a
        // priority-10 stub that permanently shadows the base response.
        var baseline = new[] { RestStep("Orders", "list", """{"v":1}""") };
        var config = ConfigWithRule("Orders", "list",
            new MockRulePredicate { JsonPath = jsonPath, EqualTo = equals, Contains = contains },
            new { v = 2 });

        var result = MockConfigApplier.ApplyToStubs(baseline, config);

        Assert.Single(result); // no rule stub compiled
    }

    [Fact]
    public void ApplyToStubs_Rule_Without_Matching_Base_Stub_Is_Skipped()
    {
        var baseline = new[] { RestStep("Orders", "list", """{"v":1}""") };
        var config = ConfigWithRule("Users", "get",
            new MockRulePredicate { JsonPath = "$.x", EqualTo = "y" },
            new { v = 2 });

        var result = MockConfigApplier.ApplyToStubs(baseline, config);

        Assert.Single(result); // only the base clone, no rule stub
    }

    [Fact]
    public void ApplyToStubs_Rule_Without_Predicate_Or_Response_Is_Skipped()
    {
        var baseline = new[] { RestStep("Orders", "list", """{"v":1}""") };
        var config = new MockConfiguration();
        // No `when` → not conditional.
        config.ConditionalRules.Add(new MockConditionalRule
        {
            Service = "Orders", Method = "list",
            Response = JsonSerializer.SerializeToElement(new { v = 2 }),
        });
        // No `response` → nothing to serve.
        config.ConditionalRules.Add(new MockConditionalRule
        {
            Service = "Orders", Method = "list",
            When = new MockRulePredicate { JsonPath = "$.x", EqualTo = "y" },
        });

        var result = MockConfigApplier.ApplyToStubs(baseline, config);

        Assert.Single(result); // both rules skipped
    }

    [Fact]
    public void ApplyToStubs_Null_Config_Returns_Cloned_Baseline()
    {
        var baseline = new[] { RestStep("Orders", "list", """{"v":1}""") };

        var result = MockConfigApplier.ApplyToStubs(baseline, null);

        Assert.Single(result);
        Assert.NotSame(baseline[0], result[0]); // a clone, not the same instance
        Assert.Equal("""{"v":1}""", result[0].Response);
    }
}
