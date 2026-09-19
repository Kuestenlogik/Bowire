// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Protocol.GraphQL.Tests;

/// <summary>
/// Automatic Persisted Queries (#713).
/// </summary>
/// <remarks>
/// Most of what can go wrong here is in reading the server's answer, not in
/// computing the hash. An APQ miss arrives as HTTP 200 with a GraphQL
/// error, so a client that watches status codes sees success and a client
/// that watches one of the two places servers put the code loses APQ
/// against the other half of them.
/// </remarks>
public sealed class GraphQLPersistedQueryTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void The_Hash_Is_Lower_Case_Hex_Sha256_Of_The_Document()
    {
        // Against values computed outside this code base, because the
        // server computes the hash independently and one that only agrees
        // with our own implementation would be worthless:
        //
        //   printf '%s' '{hello}'       | sha256sum
        //   printf '%s' 'query A { a }' | sha256sum
        Assert.Equal(
            "9dd7ff987fac8d0d1979084ebde5ce8bd855cd066d1a34e98432275cc6bc264c",
            GraphQLPersistedQuery.Hash("{hello}"));
        Assert.Equal(
            "7d0eedabb966107835cf307a0ebaf93b5d2cb8c30228611ffe3d27a53c211a0c",
            GraphQLPersistedQuery.Hash("query A { a }"));
    }

    [Fact]
    public void The_Document_Is_Hashed_Exactly_As_Written()
    {
        // No trimming, no normalising. The server hashes the bytes it
        // stored, so tidying here produces a hash that never matches and an
        // exchange that always costs two requests instead of one.
        Assert.NotEqual(
            GraphQLPersistedQuery.Hash("query A { a }"),
            GraphQLPersistedQuery.Hash(" query A { a } "));
        Assert.NotEqual(
            GraphQLPersistedQuery.Hash("query A { a }"),
            GraphQLPersistedQuery.Hash("query A {  a }"));
    }

    [Fact]
    public void The_Extension_Has_The_Shape_Servers_Look_For()
    {
        var json = JsonSerializer.Serialize(GraphQLPersistedQuery.Extension("abc"), Web);

        using var doc = JsonDocument.Parse(json);
        var pq = doc.RootElement.GetProperty("persistedQuery");
        Assert.Equal(1, pq.GetProperty("version").GetInt32());
        Assert.Equal("abc", pq.GetProperty("sha256Hash").GetString());
    }

    [Fact]
    public void A_Miss_In_The_Extensions_Code_Is_Recognised()
    {
        Assert.True(GraphQLPersistedQuery.IsMiss(Parse("""
            {"errors":[{"message":"unknown","extensions":{"code":"PERSISTED_QUERY_NOT_FOUND"}}]}
            """)));
    }

    [Fact]
    public void A_Miss_Stated_Only_In_The_Message_Is_Recognised_Too()
    {
        // Both are in the wild — the APQ write-up's own examples put it in
        // the message. Reading one place only loses APQ silently against
        // every server that chose the other.
        Assert.True(GraphQLPersistedQuery.IsMiss(Parse("""
            {"errors":[{"message":"PersistedQueryNotFound: PERSISTED_QUERY_NOT_FOUND"}]}
            """)));
    }

    [Fact]
    public void A_Successful_Answer_Is_Not_A_Miss()
    {
        Assert.False(GraphQLPersistedQuery.IsMiss(Parse("""{"data":{"a":1}}""")));
        // Nor is an ordinary GraphQL error. Retrying with the full document
        // would turn one failed call into two.
        Assert.False(GraphQLPersistedQuery.IsMiss(Parse("""
            {"errors":[{"message":"Field 'nope' does not exist"}]}
            """)));
    }

    [Fact]
    public void Not_Supported_Is_A_Miss_And_Also_Permanent()
    {
        // Both mean "send the document", so both are a miss. Only this one
        // is worth remembering: a server that does not do APQ will not
        // start during a session, and repeating the hash every call would
        // double every request for nothing.
        var response = Parse("""
            {"errors":[{"extensions":{"code":"PERSISTED_QUERY_NOT_SUPPORTED"}}]}
            """);

        Assert.True(GraphQLPersistedQuery.IsMiss(response));
        Assert.True(GraphQLPersistedQuery.IsPermanentlyUnsupported(response));
    }

    [Fact]
    public void Not_Found_Is_A_Miss_But_Not_Permanent()
    {
        // The difference that matters: this server does APQ, it just has
        // not seen this document. Disabling APQ here would forfeit it for
        // every later call to a server that supports it.
        var response = Parse("""
            {"errors":[{"extensions":{"code":"PERSISTED_QUERY_NOT_FOUND"}}]}
            """);

        Assert.True(GraphQLPersistedQuery.IsMiss(response));
        Assert.False(GraphQLPersistedQuery.IsPermanentlyUnsupported(response));
    }

    [Fact]
    public void Shapes_That_Are_Not_An_Errors_Array_Are_Not_Misses()
    {
        Assert.False(GraphQLPersistedQuery.IsMiss(Parse("""{"errors":"a string"}""")));
        Assert.False(GraphQLPersistedQuery.IsMiss(Parse("""{"errors":[]}""")));
        Assert.False(GraphQLPersistedQuery.IsMiss(Parse("""[1,2,3]""")));
        Assert.False(GraphQLPersistedQuery.IsMiss(Parse("""null""")));
    }
}
