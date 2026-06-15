// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Tests for the deterministic rule-based threat ranker
/// (<see cref="ThreatHeuristic"/>). Each test pins one rule by feeding
/// an endpoint that trips exactly that rule, then asserts both the
/// score contribution and the rule-trace text — the trace is the
/// operator-visible "why did this rank where it ranked?" explanation
/// so we deliberately assert on it rather than treating it as
/// implementation detail.
/// </summary>
public sealed class ThreatHeuristicTests
{
    private static ThreatHeuristic.Endpoint Endpoint(
        string path,
        string? verb = "GET",
        string? authState = "authenticated",
        string? inputShape = null,
        string id = "ep-1")
        => new(
            EndpointId: id,
            Path: path,
            Verb: verb,
            Protocol: "rest",
            Service: "api",
            InputShape: inputShape,
            AuthState: authState);

    [Fact]
    public void Empty_Endpoint_Array_Returns_Empty_Ranking()
    {
        var result = ThreatHeuristic.Rank([], topN: 10);

        Assert.NotNull(result);
        Assert.Empty(result.Ranked);
    }

    [Fact]
    public void Plain_Get_With_Auth_Hits_No_Rules_And_Falls_Through_To_Baseline_Phrase()
    {
        var result = ThreatHeuristic.Rank(
            [Endpoint("/ping", verb: "GET", authState: "authenticated")],
            topN: 10);

        var row = Assert.Single(result.Ranked);
        Assert.Equal(0, row.Risk);
        Assert.Equal("no rule fired — baseline low risk", row.Why);
        Assert.Empty(row.RuleTrace);
        Assert.Empty(row.SuggestedTemplates);
    }

    [Fact]
    public void Delete_Verb_Adds_Three_And_Suggests_Auth_Bypass()
    {
        var result = ThreatHeuristic.Rank(
            [Endpoint("/widgets", verb: "DELETE", authState: "authenticated")],
            topN: 10);

        var row = Assert.Single(result.Ranked);
        Assert.Equal(3, row.Risk);
        Assert.Contains("verb DELETE", row.Why, StringComparison.Ordinal);
        Assert.Contains("auth-bypass", row.SuggestedTemplates);
    }

    [Theory]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    public void Patch_Or_Put_Adds_Two_And_Suggests_Mass_Assignment(string verb)
    {
        var result = ThreatHeuristic.Rank(
            [Endpoint("/widgets", verb: verb, authState: "authenticated")],
            topN: 10);

        var row = Assert.Single(result.Ranked);
        Assert.Equal(2, row.Risk);
        Assert.Contains($"verb {verb}", row.Why, StringComparison.Ordinal);
        Assert.Contains("mass-assignment", row.SuggestedTemplates);
    }

    [Fact]
    public void Post_Verb_Adds_One_With_No_Suggested_Template()
    {
        var result = ThreatHeuristic.Rank(
            [Endpoint("/widgets", verb: "POST", authState: "authenticated")],
            topN: 10);

        var row = Assert.Single(result.Ranked);
        Assert.Equal(1, row.Risk);
        Assert.Contains("verb POST", row.Why, StringComparison.Ordinal);
        Assert.Empty(row.SuggestedTemplates);
    }

    [Theory]
    [InlineData("/widgets/{id}")]
    [InlineData("/widgets/:id")]
    [InlineData("/widgets/42")]
    [InlineData("/widgets/42/")]
    public void Id_In_Path_Adds_Two_And_Suggests_Idor(string path)
    {
        var result = ThreatHeuristic.Rank(
            [Endpoint(path, verb: "GET", authState: "authenticated")],
            topN: 10);

        var row = Assert.Single(result.Ranked);
        Assert.Equal(2, row.Risk);
        Assert.Contains("BOLA candidate", row.Why, StringComparison.Ordinal);
        Assert.Contains("idor", row.SuggestedTemplates);
    }

    [Fact]
    public void Admin_Path_Adds_Three_And_Suggests_Auth_Bypass()
    {
        var result = ThreatHeuristic.Rank(
            [Endpoint("/admin/users", verb: "GET", authState: "authenticated")],
            topN: 10);

        var row = Assert.Single(result.Ranked);
        // /admin (+3) + user-data /users (+1) → 4
        Assert.Equal(4, row.Risk);
        Assert.Contains("/admin", row.Why, StringComparison.Ordinal);
        Assert.Contains("auth-bypass", row.SuggestedTemplates);
    }

    [Fact]
    public void Auth_Path_Adds_Two_And_Suggests_Jwt_Flaws()
    {
        var result = ThreatHeuristic.Rank(
            [Endpoint("/auth/token", verb: "GET", authState: "authenticated")],
            topN: 10);

        var row = Assert.Single(result.Ranked);
        // /auth (+2) + /token (also matches AuthPath but only one regex
        // hit since both are in the same alternation set) → 2
        Assert.Equal(2, row.Risk);
        Assert.Contains("auth surface", row.Why, StringComparison.Ordinal);
        Assert.Contains("jwt-flaws", row.SuggestedTemplates);
    }

    [Fact]
    public void Anonymous_Auth_State_Adds_Two()
    {
        var result = ThreatHeuristic.Rank(
            [Endpoint("/ping", verb: "GET", authState: "anonymous")],
            topN: 10);

        var row = Assert.Single(result.Ranked);
        Assert.Equal(2, row.Risk);
        Assert.Contains("anonymous", row.Why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("UNKNOWN")]
    public void Missing_Or_Unknown_Auth_State_Adds_One(string? authState)
    {
        var result = ThreatHeuristic.Rank(
            [Endpoint("/ping", verb: "GET", authState: authState)],
            topN: 10);

        var row = Assert.Single(result.Ranked);
        Assert.Equal(1, row.Risk);
        Assert.Contains("auth state unknown", row.Why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ password: string }")]
    [InlineData("{ api_key: string }")]
    [InlineData("{ apiKey: string, ssn: string }")]
    [InlineData("{ credit_card: string }")]
    public void Sensitive_Input_Adds_Two_And_Suggests_Data_Exposure(string input)
    {
        var result = ThreatHeuristic.Rank(
            [Endpoint("/login", verb: "GET", authState: "authenticated", inputShape: input)],
            topN: 10);

        var row = Assert.Single(result.Ranked);
        // /login matches AuthPath (+2) and PiiHint (+2) → 4
        Assert.Equal(4, row.Risk);
        Assert.Contains("sensitive field name", row.Why, StringComparison.Ordinal);
        Assert.Contains("sensitive-data-exposure", row.SuggestedTemplates);
    }

    [Fact]
    public void Score_Clamps_To_Ten_When_Rules_Overflow()
    {
        // DELETE (+3) + path id (+2) + /admin (+3) + /auth (+2) +
        // /users (+1) + anonymous (+2) + PII input (+2) = 15 → clamp 10.
        var ep = new ThreatHeuristic.Endpoint(
            EndpointId: "kitchen-sink",
            Path: "/admin/auth/users/{id}",
            Verb: "DELETE",
            Protocol: "rest",
            Service: "api",
            InputShape: "{ password: string, ssn: string }",
            AuthState: "anonymous");

        var result = ThreatHeuristic.Rank([ep], topN: 10);

        var row = Assert.Single(result.Ranked);
        Assert.Equal(10, row.Risk);
        Assert.True(row.RuleTrace.Length >= 5,
            $"expected 5+ trace entries for kitchen-sink, got {row.RuleTrace.Length}");
    }

    [Fact]
    public void Suggested_Templates_Deduplicate_When_Same_Template_Hits_Twice()
    {
        // DELETE adds "auth-bypass" and /admin also adds "auth-bypass"
        // — the distinct pass should collapse to one entry.
        var ep = Endpoint("/admin/widgets", verb: "DELETE", authState: "authenticated");
        var result = ThreatHeuristic.Rank([ep], topN: 10);

        var row = Assert.Single(result.Ranked);
        Assert.Equal(1, row.SuggestedTemplates.Count(t =>
            string.Equals(t, "auth-bypass", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Rank_Sorts_By_Risk_Descending()
    {
        var endpoints = new[]
        {
            Endpoint("/health", verb: "GET", authState: "authenticated", id: "low"),
            Endpoint("/admin/widgets/{id}", verb: "DELETE", authState: "anonymous", id: "high"),
            Endpoint("/widgets", verb: "POST", authState: "authenticated", id: "mid"),
        };

        var result = ThreatHeuristic.Rank(endpoints, topN: 10);

        Assert.Equal(3, result.Ranked.Length);
        Assert.Equal("high", result.Ranked[0].EndpointId);
        Assert.Equal("mid", result.Ranked[1].EndpointId);
        Assert.Equal("low", result.Ranked[2].EndpointId);
        Assert.True(result.Ranked[0].Risk >= result.Ranked[1].Risk);
        Assert.True(result.Ranked[1].Risk >= result.Ranked[2].Risk);
    }

    [Fact]
    public void TopN_Truncates_Ranked_List()
    {
        var endpoints = new[]
        {
            Endpoint("/a", verb: "DELETE", authState: "anonymous", id: "a"),
            Endpoint("/b", verb: "PATCH", authState: "anonymous", id: "b"),
            Endpoint("/c", verb: "POST", authState: "anonymous", id: "c"),
            Endpoint("/d", verb: "GET", authState: "authenticated", id: "d"),
        };

        var result = ThreatHeuristic.Rank(endpoints, topN: 2);

        Assert.Equal(2, result.Ranked.Length);
        Assert.Equal("a", result.Ranked[0].EndpointId);
        Assert.Equal("b", result.Ranked[1].EndpointId);
    }

    [Fact]
    public void TopN_Zero_Or_Negative_Returns_All_Rows()
    {
        // The clamp guard is `topN > 0 && ranked.Count > topN`; both
        // 0 and -1 skip the truncation and return everything.
        var endpoints = new[]
        {
            Endpoint("/a", verb: "GET", id: "a"),
            Endpoint("/b", verb: "GET", id: "b"),
            Endpoint("/c", verb: "GET", id: "c"),
        };

        var zero = ThreatHeuristic.Rank(endpoints, topN: 0);
        Assert.Equal(3, zero.Ranked.Length);

        var negative = ThreatHeuristic.Rank(endpoints, topN: -1);
        Assert.Equal(3, negative.Ranked.Length);
    }

    [Fact]
    public void Get_Head_And_Options_Verbs_Have_No_Baseline_Bump()
    {
        // GET / HEAD / OPTIONS are intentionally the no-op path in the
        // verb switch; pin that so a future "treat HEAD as risky" pass
        // is forced to update the test.
        foreach (var verb in new[] { "GET", "HEAD", "OPTIONS" })
        {
            var result = ThreatHeuristic.Rank(
                [Endpoint("/safe", verb: verb, authState: "authenticated", id: verb)],
                topN: 10);
            var row = Assert.Single(result.Ranked);
            Assert.Equal(0, row.Risk);
        }
    }

    [Fact]
    public void RankedRow_Endpoint_Id_Is_Preserved_For_Lookup()
    {
        var result = ThreatHeuristic.Rank(
            [Endpoint("/widgets/{id}", verb: "DELETE", authState: "anonymous", id: "custom-id-123")],
            topN: 10);

        var row = Assert.Single(result.Ranked);
        Assert.Equal("custom-id-123", row.EndpointId);
    }
}
