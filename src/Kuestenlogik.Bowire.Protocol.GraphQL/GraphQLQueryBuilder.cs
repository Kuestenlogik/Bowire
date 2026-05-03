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
/// We don't try to walk the full output type — the inner selection set is
/// always <c>__typename</c>, which is universally valid and lets the user
/// see whether the call routed correctly. Real selection sets (with the
/// fields the user actually wants) are tracked as a follow-up.
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

        // Universal valid selection — every GraphQL type supports __typename.
        // The user can override the operation in the JSON editor when they
        // want a richer selection set.
        sb.Append(" {\n    __typename\n  }\n}");

        return (sb.ToString(), variables);
    }

    /// <summary>
    /// Maps a Bowire field back to a GraphQL type literal — e.g. an
    /// <c>int32 required repeated</c> field becomes <c>[Int!]!</c>.
    /// We need this because GraphQL operation variables are typed at the
    /// operation level, not just at the call site.
    /// </summary>
    private static string BuildArgType(BowireFieldInfo field)
    {
        var inner = field.Type switch
        {
            "int32" or "int64" or "uint32" or "uint64" => "Int",
            "float" or "double" => "Float",
            "bool" => "Boolean",
            "message" => field.MessageType?.Name ?? "JSON",
            _ => "String"
        };

        // The discovered field stores ID and custom scalars as "string", so we
        // can't perfectly round-trip them — fall back to String, which any
        // GraphQL server with implicit ID coercion will accept.

        if (field.IsRepeated)
            inner = "[" + inner + "!]";

        if (field.Required)
            inner += "!";

        return inner;
    }
}
