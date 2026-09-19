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
            // not expose a documented syntax-error type on this surface, and
            // this is not our error to report anyway. The document goes to
            // the server as typed and the server's message is the better one
            // -- it knows the schema, we only know the grammar.
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
