// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Protocol.GraphQL.Tests;

/// <summary>
/// Queries over <c>GET</c> (#713).
/// </summary>
/// <remarks>
/// <para>
/// Bowire posted everything. That is the right default and stays the
/// default; what it left unreachable was a CDN or cache in front of the
/// API, which can only cache a GET, and a server that accepts queries over
/// GET alone.
/// </para>
/// <para>
/// The guard is the part worth reading. GraphQL over GET is defined for
/// queries only, and not out of pedantry: intermediaries are entitled to
/// retry, prefetch and cache a GET. A mutation behind that verb is a write
/// somebody else may decide to repeat.
/// </para>
/// </remarks>
public sealed class GraphQLGetTransportTests
{
    private static JsonElement Variables(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void The_Document_Is_Carried_In_The_Query_String()
    {
        var uri = BowireGraphQLProtocol.BuildGetUri(
            "https://api.example.com/graphql", "query A { a }", null, "A");

        // AbsoluteUri, not ToString(): ToString() hands back the unescaped
        // form, which is exactly the thing that must NOT go on the wire.
        Assert.StartsWith("https://api.example.com/graphql?", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("query=query%20A%20%7B%20a%20%7D", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("operationName=A", uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Variables_Travel_As_A_Json_String()
    {
        // The GraphQL-over-HTTP convention: not one parameter per variable,
        // one JSON document under `variables`. A server parses it as JSON,
        // so splitting it would lose every type that is not a string.
        var uri = BowireGraphQLProtocol.BuildGetUri(
            "https://api.example.com/graphql",
            "query A($id: ID!, $n: Int) { a }",
            Variables("""{"id":"b1","n":3}"""),
            "A");

        Assert.Contains("variables=", uri.ToString(), StringComparison.Ordinal);
        var value = System.Web.HttpUtility.ParseQueryString(uri.Query)["variables"];
        using var back = JsonDocument.Parse(value!);
        Assert.Equal("b1", back.RootElement.GetProperty("id").GetString());
        Assert.Equal(3, back.RootElement.GetProperty("n").GetInt32());
    }

    [Fact]
    public void An_Empty_Variables_Object_Is_Left_Out()
    {
        // It carries nothing and the URL is the tightest budget on this
        // path — a document long enough to matter is exactly the one that
        // meets a length limit nobody controls.
        var uri = BowireGraphQLProtocol.BuildGetUri(
            "https://api.example.com/graphql", "query A { a }", Variables("{}"), null);

        Assert.DoesNotContain("variables=", uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_Anonymous_Operation_Sends_No_Operation_Name()
    {
        var uri = BowireGraphQLProtocol.BuildGetUri(
            "https://api.example.com/graphql", "{ a }", null, null);

        Assert.DoesNotContain("operationName", uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_Endpoint_That_Already_Has_A_Query_String_Keeps_It()
    {
        // Some deployments route on one. Replacing it would send the
        // request somewhere else entirely, and the failure would look like
        // a GraphQL problem rather than a routing one.
        var uri = BowireGraphQLProtocol.BuildGetUri(
            "https://api.example.com/graphql?tenant=harbour", "query A { a }", null, null);

        Assert.Contains("tenant=harbour", uri.ToString(), StringComparison.Ordinal);
        Assert.Contains("&query=", uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Reserved_Characters_In_The_Document_Survive_Encoding()
    {
        // A selection set is nothing but reserved characters. If any of
        // them reached the URL raw, the server would read a truncated
        // document and complain about syntax the operator did write
        // correctly.
        var uri = BowireGraphQLProtocol.BuildGetUri(
            "https://api.example.com/graphql",
            """query A { f(s: "a&b=c#d") { __typename } }""", null, null);

        var value = System.Web.HttpUtility.ParseQueryString(uri.Query)["query"];
        Assert.Equal("""query A { f(s: "a&b=c#d") { __typename } }""", value);
    }

    [Theory]
    [InlineData("query A { a }", "query")]
    [InlineData("{ a }", "query")]                       // shorthand is a query
    [InlineData("mutation M { m }", "mutation")]
    [InlineData("subscription S { s }", "subscription")]
    public void The_Document_Says_What_Kind_Of_Operation_It_Is(string document, string expected)
    {
        // Read from the parser's operation type rather than a keyword,
        // because the shorthand form writes no keyword at all.
        Assert.Equal([expected], GraphQLDocumentInfo.OperationKinds(document));
    }

    [Fact]
    public void A_Document_That_Does_Not_Parse_Claims_No_Kind()
    {
        // The caller then falls back to what it expected, which leaves the
        // pre-existing behaviour in place rather than guessing.
        Assert.Empty(GraphQLDocumentInfo.OperationKinds("query Broken { unclosed "));
        Assert.Empty(GraphQLDocumentInfo.OperationKinds(""));
        Assert.Empty(GraphQLDocumentInfo.OperationKinds(null));
    }
}
