// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.GraphQL;

/// <summary>
/// Builds an executable GraphQL operation string from a discovered method
/// (<see cref="BowireMethodInfo"/>) and a runtime variables payload. The
/// goal is to take a Bowire form submission like
/// <c>{ "id": "abc" }</c> and turn it into a self-contained operation:
/// <code>
/// query getUser($id: ID!) {
///   getUser(id: $id) { id name email }
/// }
/// </code>
/// #710 — the inner selection set used to be <c>__typename</c> and nothing
/// else. Valid, and useless outside the workbench: the UI has its own field
/// picker and sends a finished query, so only the callers without a UI —
/// the CLI, flows, contract tests — ever saw the generated operation, and
/// what came back was the type's name. It is now walked from the discovered
/// output type.
/// </summary>
internal static class GraphQLQueryBuilder
{
    /// <summary>
    /// Builds the operation string + variables payload for a discovered method.
    /// </summary>
    /// <param name="operationKind">"query", "mutation", or "subscription"</param>
    /// <param name="method">The discovered method whose <see cref="BowireMethodInfo.Name"/> is the field on the root type and whose <see cref="BowireMethodInfo.InputType"/> describes the arguments.</param>
    /// <param name="variablesJson">JSON object the user filled in via the form (may be empty).</param>
    public static (string Operation, JsonElement Variables) Build(
        string operationKind,
        BowireMethodInfo method,
        string variablesJson)
    {
        JsonElement variables;
        try
        {
            variables = string.IsNullOrWhiteSpace(variablesJson)
                ? JsonDocument.Parse("{}").RootElement
                : JsonDocument.Parse(variablesJson).RootElement;
        }
        catch
        {
            variables = JsonDocument.Parse("{}").RootElement;
        }

        var fields = method.InputType?.Fields ?? [];
        var sb = new StringBuilder();
        sb.Append(operationKind).Append(' ').Append(method.Name);

        if (fields.Count > 0)
        {
            sb.Append('(');
            for (var i = 0; i < fields.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('$').Append(fields[i].Name).Append(": ").Append(BuildArgType(fields[i]));
            }
            sb.Append(')');
        }

        sb.Append(" {\n  ").Append(method.Name);

        if (fields.Count > 0)
        {
            sb.Append('(');
            for (var i = 0; i < fields.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(fields[i].Name).Append(": $").Append(fields[i].Name);
            }
            sb.Append(')');
        }

        // #710 - the selection set comes from the discovered output type.
        // __typename stays the fallback for a type nothing is known about,
        // because an empty selection set is a syntax error.
        sb.Append(" {\n").Append(BuildSelectionSet(method.OutputType, "    ", depth: 0))
          .Append("\n  }\n}");

        return (sb.ToString(), variables);
    }

    /// <summary>
    /// How deep the generated selection set goes. Past this a
    /// self-referential type - a Node whose children are Nodes - would
    /// generate forever.
    /// </summary>
    private const int MaxSelectionDepth = 3;

    /// <summary>
    /// A selection set for a discovered output type (#710).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every scalar is selected and every object field is recursed into.
    /// That is the rule the workbench's own field picker starts from, so a
    /// caller without a UI gets what a caller with one would have seen
    /// before touching a checkbox.
    /// </para>
    /// <para>
    /// <c>__typename</c> is the fallback wherever there is nothing else to
    /// say: no fields, past the depth cap, or a shape discovery chose not
    /// to expand. An empty selection set is a syntax error, so the fallback
    /// is what keeps the operation valid rather than merely uninformative.
    /// </para>
    /// </remarks>
    private static string BuildSelectionSet(BowireMessageInfo? type, string indent, int depth)
    {
        if (type is null || type.Fields.Count == 0 || depth >= MaxSelectionDepth)
            return indent + "__typename";

        var sb = new StringBuilder();
        foreach (var field in type.Fields)
        {
            if (sb.Length > 0) sb.Append('\n');

            if (field.Type == "message" && field.MessageType is { } nested)
            {
                sb.Append(indent).Append(field.Name).Append(" {\n")
                  .Append(BuildSelectionSet(nested, indent + "  ", depth + 1))
                  .Append('\n').Append(indent).Append('}');
            }
            else
            {
                sb.Append(indent).Append(field.Name);
            }
        }

        return sb.Length == 0 ? indent + "__typename" : sb.ToString();
    }

    /// <summary>
    /// Maps a Bowire field back to a GraphQL type literal — e.g. an
    /// <c>int32 required repeated</c> field becomes <c>[Int!]!</c>.
    /// We need this because GraphQL operation variables are typed at the
    /// operation level, not just at the call site.
    /// </summary>
    private static string BuildArgType(BowireFieldInfo field)
    {
        // #713 - the schema's own name for the type when discovery kept it.
        // Without this, ID, every enum and every custom scalar came out as
        // String, because that is what they all normalise to in Bowire's
        // shared vocabulary. `$id: String!` against an `ID!` argument is
        // refused by any server that does not do implicit coercion, and an
        // enum argument is refused by all of them.
        var inner = !string.IsNullOrWhiteSpace(field.SchemaType)
            ? field.SchemaType
            : field.Type switch
            {
                // The fallback path, for a field that reached here without
                // discovery — a stub built from the request body. It cannot
                // do better than guess from the JSON value's shape.
                "int32" or "int64" or "uint32" or "uint64" => "Int",
                "float" or "double" => "Float",
                "bool" => "Boolean",
                "message" => field.MessageType?.Name ?? "JSON",
                _ => "String"
            };

        if (field.IsRepeated)
            inner = "[" + inner + "!]";

        if (field.Required)
            inner += "!";

        return inner;
    }
}
