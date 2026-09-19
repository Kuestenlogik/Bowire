// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using GraphQLParser;
using GraphQLParser.AST;

namespace Kuestenlogik.Bowire.Protocol.GraphQL;

/// <summary>
/// What a hand-written GraphQL document says about itself: which
/// operations it declares, and which one a request should name.
/// </summary>
/// <remarks>
/// <para>
/// #710 — until this existed, Bowire never sent <c>operationName</c>. For a
/// single-operation document that is harmless; the server runs the only
/// thing there is. For a document with several, the spec requires the field,
/// so the request is rejected — and a request builder whose editor is one
/// textarea invites exactly that document.
/// </para>
/// <para>
/// Parsed rather than pattern-matched. The plugin already depends on
/// GraphQL-Parser for the schema-only mock, so a regex here would have been
/// a second, worse answer to the same question: a leading <c>#</c> comment,
/// the word <c>query</c> inside a string literal, or a leading fragment
/// definition each break the obvious pattern, and all three are ordinary
/// things to write.
/// </para>
/// </remarks>
internal static class GraphQLDocumentInfo
{
    /// <summary>
    /// The operations a document declares, in source order. An anonymous
    /// operation contributes <c>null</c> — it is an operation, it just has
    /// no name to send.
    /// </summary>
    public static IReadOnlyList<string?> Operations(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        GraphQLDocument document;
        try
        {
            document = Parser.Parse(query);
        }
        catch (Exception)
        {
            // Catch-all, like the mock's own parse site: GraphQLParser does
            // not expose a documented syntax-error type here. Callers that
            // care about the reason use SyntaxError; this one only wants
            // the operations, and a document that does not parse has none
            // it can name.
            return [];
        }

        var names = new List<string?>();
        foreach (var definition in document.Definitions)
        {
            if (definition is GraphQLOperationDefinition op)
                names.Add(op.Name?.StringValue);
        }
        return names;
    }

    /// <summary>
    /// The operation keywords a document declares, in source order (#713).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lower-case <c>query</c>, <c>mutation</c> or <c>subscription</c>. The
    /// GraphQL shorthand <c>{ field }</c> is a query and reports itself as
    /// one, which is why this reads the parser's operation type rather than
    /// looking for a keyword that may not be written.
    /// </para>
    /// <para>
    /// The caller that needs this is the GET guard: the rail may believe a
    /// tab holds a query while the document pasted into it declares a
    /// mutation, and sending that over GET puts a write behind a verb
    /// intermediaries treat as safe to repeat.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> OperationKinds(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        GraphQLDocument document;
        try
        {
            document = Parser.Parse(query);
        }
        catch (Exception)
        {
            return [];
        }

        var kinds = new List<string>();
        foreach (var definition in document.Definitions)
        {
            if (definition is not GraphQLOperationDefinition op) continue;
            kinds.Add(op.Operation switch
            {
                OperationType.Mutation => "mutation",
                OperationType.Subscription => "subscription",
                _ => "query",
            });
        }
        return kinds;
    }

    /// <summary>
    /// The parser's complaint about a document, or <c>null</c> when it
    /// parses (#710).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Grammar only. A field that does not exist, an argument of the wrong
    /// type, a fragment on the wrong type — none of that is visible here,
    /// and the server's answer is the better one for all of it, because the
    /// server knows the schema.
    /// </para>
    /// <para>
    /// What this catches is the unclosed brace. Before it, a typo in the
    /// editor became an HTTP round trip and whatever wording the server
    /// chose; now it is answered where it was made, with a position.
    /// </para>
    /// <para>
    /// The risk this accepts, stated because it is not zero: a document
    /// this parser version rejects and a server would have accepted is
    /// refused here. That is why the message says who is speaking — an
    /// operator who sees Bowire's name on it knows to look at the parser
    /// rather than at their server.
    /// </para>
    /// </remarks>
    public static string? SyntaxError(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        try
        {
            Parser.Parse(query);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// The <c>operationName</c> to put on the wire, or <c>null</c> for none.
    /// </summary>
    /// <param name="query">The document as typed.</param>
    /// <param name="requested">
    /// A name the caller asked for explicitly — from the payload's
    /// <c>operationName</c>. Wins outright: the caller knows which operation
    /// they mean, and second-guessing them would run a different one.
    /// </param>
    /// <remarks>
    /// With several operations and nothing requested this returns
    /// <c>null</c> on purpose. Picking the first would run an operation the
    /// caller did not choose and report it as a success; the server's
    /// "must provide operation name" is the answer that can be acted on.
    /// </remarks>
    public static string? ResolveOperationName(string? query, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();

        var operations = Operations(query);
        if (operations.Count != 1) return null;
        return string.IsNullOrEmpty(operations[0]) ? null : operations[0];
    }
}
