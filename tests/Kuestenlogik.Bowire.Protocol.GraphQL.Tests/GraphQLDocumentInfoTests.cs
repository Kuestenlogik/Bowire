// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.GraphQL;

namespace Kuestenlogik.Bowire.Protocol.GraphQL.Tests;

/// <summary>
/// Which operation a hand-written document means (#710).
/// </summary>
/// <remarks>
/// Bowire sent no <c>operationName</c> at all until this existed. The cases
/// that matter are the ones a regex gets wrong — a leading comment, the word
/// <c>query</c> inside a string, a fragment before the operation — because
/// the request builder's editor is one textarea and all three are ordinary
/// things to type into it.
/// </remarks>
public sealed class GraphQLDocumentInfoTests
{
    [Fact]
    public void One_Named_Operation_Is_Resolved_Without_Being_Asked()
    {
        // Harmless and useful: the server would run it either way, but the
        // name reaches the logs on both ends.
        Assert.Equal("GetUser", GraphQLDocumentInfo.ResolveOperationName(
            "query GetUser($id: ID!) { user(id: $id) { id } }", null));
    }

    [Fact]
    public void An_Anonymous_Operation_Has_No_Name_To_Send()
    {
        Assert.Null(GraphQLDocumentInfo.ResolveOperationName("{ user { id } }", null));
        Assert.Null(GraphQLDocumentInfo.ResolveOperationName("query { user { id } }", null));
    }

    [Fact]
    public void Several_Operations_And_No_Choice_Sends_Nothing()
    {
        // The point of the whole change. Picking the first would run an
        // operation nobody chose and report it as a success; no name lets
        // the server answer "must provide operation name", which can be
        // acted on.
        const string doc = "query A { a }\nmutation B { b }";
        Assert.Null(GraphQLDocumentInfo.ResolveOperationName(doc, null));
    }

    [Fact]
    public void An_Explicit_Choice_Wins_Even_Against_A_Single_Operation()
    {
        // The caller knows which operation they mean. Overriding them would
        // run a different one.
        Assert.Equal("B", GraphQLDocumentInfo.ResolveOperationName("query A { a }", "B"));
        Assert.Equal("B", GraphQLDocumentInfo.ResolveOperationName("query A { a }\nquery B { b }", " B "));
    }

    [Fact]
    public void Blank_And_Missing_Choices_Are_Not_Choices()
    {
        Assert.Equal("A", GraphQLDocumentInfo.ResolveOperationName("query A { a }", "   "));
        Assert.Equal("A", GraphQLDocumentInfo.ResolveOperationName("query A { a }", ""));
    }

    [Theory]
    // A comment that looks like an operation.
    [InlineData("# query Ghost { x }\nquery Real { y }")]
    // A string literal that looks like one.
    [InlineData("query Real { field(note: \"query Ghost { x }\") }")]
    // A fragment ahead of the operation, so the document does not begin
    // with the keyword.
    [InlineData("fragment F on T { id }\nquery Real { ...F }")]
    public void What_Looks_Like_A_Second_Operation_But_Is_Not(string document)
    {
        Assert.Equal("Real", GraphQLDocumentInfo.ResolveOperationName(document, null));
        Assert.Equal(["Real"], GraphQLDocumentInfo.Operations(document));
    }

    [Fact]
    public void Operations_Keeps_Source_Order_And_Records_The_Anonymous_One()
    {
        // The anonymous entry is not noise: it is why a document with one
        // named and one unnamed operation resolves to nothing. Count, not
        // names, decides.
        Assert.Equal(["A", null, "C"], GraphQLDocumentInfo.Operations(
            "query A { a }\n{ b }\nsubscription C { c }"));
    }

    [Fact]
    public void A_Document_That_Does_Not_Parse_Yields_Nothing_Rather_Than_Throwing()
    {
        // Not ours to report. The document goes to the server as typed and
        // the server's message is the better one — it knows the schema.
        Assert.Empty(GraphQLDocumentInfo.Operations("query Broken { unclosed "));
        Assert.Null(GraphQLDocumentInfo.ResolveOperationName("query Broken { unclosed ", null));
    }

    [Fact]
    public void Empty_Input_Is_Not_An_Error()
    {
        Assert.Empty(GraphQLDocumentInfo.Operations(null));
        Assert.Empty(GraphQLDocumentInfo.Operations(""));
        Assert.Empty(GraphQLDocumentInfo.Operations("   "));
    }
}
