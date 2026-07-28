// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mock.Matchers;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// #511 — query-aware matching for GraphQL steps. All GraphQL traffic
/// shares one POST route, so the matcher ranks candidates by query
/// affinity (normalised equality > root-field equality) and disqualifies
/// steps whose root fields conflict with the incoming query.
/// </summary>
public sealed class GraphQlMatchingTests
{
    private static readonly string[] s_expectedRootFields = ["portCall", "stats"];

    private static BowireRecording MakeRecording(params BowireRecordingStep[] steps)
    {
        var rec = new BowireRecording { RecordingFormatVersion = 2 };
        foreach (var s in steps) rec.Steps.Add(s);
        return rec;
    }

    private static BowireRecordingStep GraphQlStep(string query, string response, string method = "q") => new()
    {
        Id = "step_" + Guid.NewGuid().ToString("N")[..8],
        Protocol = "graphql",
        Service = "Query",
        Method = method,
        MethodType = "Unary",
        HttpVerb = "POST",
        HttpPath = "/graphql",
        Status = "OK",
        Body = $$"""{"query": {{System.Text.Json.JsonSerializer.Serialize(query)}}}""",
        Response = response
    };

    private static MockRequest GraphQlReq(string query) => new()
    {
        Protocol = "graphql",
        HttpMethod = "POST",
        Path = "/graphql",
        Body = $$"""{"query": {{System.Text.Json.JsonSerializer.Serialize(query)}}}"""
    };

    [Fact]
    public void Exact_Query_Wins_Over_Sibling_With_Same_Root_Field()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(
            GraphQlStep("{ portCall(id: 1) { id status } }", """{"data":{"portCall":{"id":1}}}"""),
            GraphQlStep("{ portCall(id: 2) { id status } }", """{"data":{"portCall":{"id":2}}}"""));

        Assert.True(matcher.TryMatch(
            GraphQlReq("{ portCall(id: 2) { id status } }"), rec, out var step));
        Assert.Equal("""{"data":{"portCall":{"id":2}}}""", step.Response);
    }

    [Fact]
    public void Whitespace_Differences_Do_Not_Defeat_Equality()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(
            GraphQlStep("{ portCall(id: 1) { id status } }", """{"data":1}"""),
            GraphQlStep("{ ships { id } }", """{"data":2}"""));

        Assert.True(matcher.TryMatch(
            GraphQlReq("{\n  portCall(id: 1) {\n    id\n    status\n  }\n}"), rec, out var step));
        Assert.Equal("""{"data":1}""", step.Response);
    }

    [Fact]
    public void Root_Field_Match_Serves_Different_Selection_Shapes()
    {
        // A portCall query with a different field selection still gets the
        // recorded portCall payload — path + root field are the identity.
        var matcher = new ExactMatcher();
        var rec = MakeRecording(
            GraphQlStep("{ portCall(id: 1) { id status ship { name } } }", """{"data":{"portCall":{}}}"""),
            GraphQlStep("{ ships { id } }", """{"data":{"ships":[]}}"""));

        Assert.True(matcher.TryMatch(
            GraphQlReq("query pc($id: Int!) { portCall(id: $id) { id } }"), rec, out var step));
        Assert.Equal("""{"data":{"portCall":{}}}""", step.Response);
    }

    [Fact]
    public void Conflicting_Root_Field_Disqualifies_Instead_Of_First_Match()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(
            GraphQlStep("{ portCall(id: 1) { id } }", """{"data":{"portCall":{}}}"""));

        // `ships` never recorded → no match, not the portCall payload.
        Assert.False(matcher.TryMatch(GraphQlReq("{ ships { id } }"), rec, out _));
    }

    [Fact]
    public void Wrong_Path_Does_Not_Match()
    {
        var matcher = new ExactMatcher();
        var rec = MakeRecording(GraphQlStep("{ portCall(id: 1) { id } }", """{"data":1}"""));

        Assert.False(matcher.TryMatch(new MockRequest
        {
            Protocol = "graphql",
            HttpMethod = "POST",
            Path = "/other",
            Body = """{"query": "{ portCall(id: 1) { id } }"}"""
        }, rec, out _));
    }

    // ---- affinity primitive ----

    [Theory]
    [InlineData("{ portCall(id: 1) { id } }", "{ portCall(id: 1) { id } }", 2)]
    [InlineData("{ portCall(id: 1) { id } }", "{\tportCall( id:  1 ) {  id }  }", 2)]
    [InlineData("{ portCall(id: 1) { id ship { name } } }", "query x { portCall(id: 9) { status } }", 1)]
    [InlineData("{ portCall(id: 1) { id } }", "{ ships { id } }", -1)]
    [InlineData("mutation { advancePortCall(id: 1) { id } }", "{ advancePortCall { id } }", 1)]
    public void GraphQlQueryAffinity_Ranks_As_Documented(string recorded, string incoming, int expected)
    {
        Assert.Equal(expected, ExactMatcher.GraphQlQueryAffinity(
            $$"""{"query": {{System.Text.Json.JsonSerializer.Serialize(recorded)}}}""",
            $$"""{"query": {{System.Text.Json.JsonSerializer.Serialize(incoming)}}}"""));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("not json", 0)]
    [InlineData("""{"noQuery": true}""", 0)]
    public void GraphQlQueryAffinity_Unknown_Bodies_Stay_Neutral(string? incomingBody, int expected)
    {
        Assert.Equal(expected, ExactMatcher.GraphQlQueryAffinity(
            """{"query": "{ portCall { id } }"}""",
            incomingBody));
    }

    [Fact]
    public void RootFieldNames_Ignores_Arguments_Nesting_Directives_And_Comments()
    {
        var fields = ExactMatcher.RootFieldNames("""
            query pc($id: Int!) @cached {
              # the aggregate
              portCall(id: $id, filter: "{brace}") @include(if: true) {
                id
                ship { name }
              }
              stats
            }
            """);

        Assert.Equal(
            s_expectedRootFields,
            fields.OrderBy(f => f, StringComparer.Ordinal).ToArray());
    }
}
