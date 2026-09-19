// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using GraphQLParser.AST;

namespace Kuestenlogik.Bowire.Protocol.GraphQL.Mock;

/// <summary>
/// Builds a <c>__schema</c> introspection response out of a parsed SDL
/// document (#710).
/// </summary>
/// <remarks>
/// <para>
/// Without this the schema-only mock answered <c>__typename</c> but not
/// <c>__schema</c>, which made GraphQL the one protocol where "start the
/// mock, point the workbench at it, discover" did not work. Every other
/// protocol's mock is discoverable by its own client; this one was not.
/// </para>
/// <para>
/// Built from the SDL syntax tree rather than from the handler's runtime
/// type index. That index is deliberately lossy — it flattens a field's
/// type to a name plus an is-a-list flag and drops arguments entirely,
/// because rendering a sample value needs nothing more. Introspection needs
/// exactly what it drops: the non-null wrappers, the argument lists, the
/// distinction between an object and an interface.
/// </para>
/// <para>
/// One deliberate inexactness, and it is visible from outside: the response
/// is the whole introspection document regardless of which fields the query
/// selected. A spec-correct server answers the selection set and nothing
/// else. Doing that here would mean resolving fragment spreads, which this
/// mock does not do anywhere, for a gain no client wants — a client asking
/// for fewer fields is not harmed by receiving more.
/// </para>
/// </remarks>
internal static class GraphQlIntrospection
{
    /// <summary>
    /// The five scalars every schema has without declaring them. Leaving
    /// them out is not cosmetic: a field of type <c>String</c> would name a
    /// type the response never lists, and a client walking the type table to
    /// render an input form finds nothing there.
    /// </summary>
    private static readonly string[] BuiltInScalars = ["String", "Int", "Float", "Boolean", "ID"];

    /// <summary>
    /// The <c>{ "data": { "__schema": … } }</c> body for a parsed SDL
    /// document, ready to write.
    /// </summary>
    public static JsonObject Build(GraphQLDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Spec defaults. An explicit `schema { … }` block overrides them
        // below; most SDL files have no such block at all.
        var roots = new Dictionary<OperationType, string>
        {
            [OperationType.Query] = "Query",
            [OperationType.Mutation] = "Mutation",
            [OperationType.Subscription] = "Subscription",
        };

        var declared = new List<ASTNode>();
        foreach (var definition in document.Definitions)
        {
            if (definition is GraphQLSchemaDefinition schema)
            {
                foreach (var op in schema.OperationTypes ?? Enumerable.Empty<GraphQLRootOperationTypeDefinition>())
                {
                    if (op.Type?.Name.StringValue is { } name) roots[op.Operation] = name;
                }
                continue;
            }
            declared.Add(definition);
        }

        var declaredNames = new HashSet<string>(StringComparer.Ordinal);
        var types = new JsonArray();
        foreach (var definition in declared)
        {
            var mapped = MapType(definition);
            if (mapped is null) continue;
            if (mapped["name"]?.GetValue<string>() is { } n) declaredNames.Add(n);
            types.Add(mapped);
        }

        foreach (var scalar in BuiltInScalars)
        {
            if (declaredNames.Contains(scalar)) continue;
            types.Add(ScalarType(scalar, description: null));
        }

        // A root is only named when the schema actually declares it. Naming
        // a Mutation root that does not exist would have the client build a
        // service whose every call fails.
        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["__schema"] = new JsonObject
                {
                    ["queryType"] = RootRef(roots[OperationType.Query], declaredNames),
                    ["mutationType"] = RootRef(roots[OperationType.Mutation], declaredNames),
                    ["subscriptionType"] = RootRef(roots[OperationType.Subscription], declaredNames),
                    ["types"] = types,
                },
            },
        };
    }

    private static JsonObject? RootRef(string name, HashSet<string> declared)
        => declared.Contains(name) ? new JsonObject { ["name"] = name } : null;

    private static JsonObject ScalarType(string name, string? description) => new()
    {
        ["kind"] = "SCALAR",
        ["name"] = name,
        ["description"] = description,
        ["fields"] = null,
        ["inputFields"] = null,
        ["enumValues"] = null,
    };

    private static JsonObject? MapType(ASTNode definition) => definition switch
    {
        GraphQLObjectTypeDefinition obj => new JsonObject
        {
            ["kind"] = "OBJECT",
            ["name"] = obj.Name.StringValue,
            ["description"] = Description(obj.Description),
            ["fields"] = MapFields(obj.Fields),
            ["inputFields"] = null,
            ["enumValues"] = null,
        },
        GraphQLInterfaceTypeDefinition iface => new JsonObject
        {
            ["kind"] = "INTERFACE",
            ["name"] = iface.Name.StringValue,
            ["description"] = Description(iface.Description),
            ["fields"] = MapFields(iface.Fields),
            ["inputFields"] = null,
            ["enumValues"] = null,
        },
        GraphQLInputObjectTypeDefinition input => new JsonObject
        {
            ["kind"] = "INPUT_OBJECT",
            ["name"] = input.Name.StringValue,
            ["description"] = Description(input.Description),
            ["fields"] = null,
            ["inputFields"] = MapInputFields(input.Fields),
            ["enumValues"] = null,
        },
        GraphQLEnumTypeDefinition en => new JsonObject
        {
            ["kind"] = "ENUM",
            ["name"] = en.Name.StringValue,
            ["description"] = Description(en.Description),
            ["fields"] = null,
            ["inputFields"] = null,
            ["enumValues"] = MapEnumValues(en),
        },
        GraphQLUnionTypeDefinition union => new JsonObject
        {
            ["kind"] = "UNION",
            ["name"] = union.Name.StringValue,
            ["description"] = Description(union.Description),
            ["fields"] = null,
            ["inputFields"] = null,
            ["enumValues"] = null,
        },
        GraphQLScalarTypeDefinition scalar =>
            ScalarType(scalar.Name.StringValue, Description(scalar.Description)),
        // Directives, type extensions and operation definitions are not types.
        _ => null,
    };

    private static JsonArray? MapFields(IEnumerable<GraphQLFieldDefinition>? fields)
    {
        if (fields is null) return null;
        var result = new JsonArray();
        foreach (var f in fields)
        {
            var (deprecated, reason) = Deprecation(f.Directives);
            result.Add(new JsonObject
            {
                ["name"] = f.Name.StringValue,
                ["description"] = Description(f.Description),
                ["args"] = MapArgs(f.Arguments),
                ["type"] = TypeRef(f.Type),
                ["isDeprecated"] = deprecated,
                ["deprecationReason"] = reason,
            });
        }
        return result;
    }

    private static JsonArray MapArgs(IEnumerable<GraphQLInputValueDefinition>? args)
    {
        var result = new JsonArray();
        if (args is null) return result;
        foreach (var a in args)
        {
            result.Add(new JsonObject
            {
                ["name"] = a.Name.StringValue,
                ["description"] = Description(a.Description),
                ["type"] = TypeRef(a.Type),
                ["defaultValue"] = a.DefaultValue?.ToString(),
            });
        }
        return result;
    }

    private static JsonArray? MapInputFields(IEnumerable<GraphQLInputValueDefinition>? fields)
    {
        if (fields is null) return null;
        var result = new JsonArray();
        foreach (var f in fields)
        {
            result.Add(new JsonObject
            {
                ["name"] = f.Name.StringValue,
                ["description"] = Description(f.Description),
                ["type"] = TypeRef(f.Type),
                ["defaultValue"] = f.DefaultValue?.ToString(),
            });
        }
        return result;
    }

    private static JsonArray MapEnumValues(GraphQLEnumTypeDefinition en)
    {
        var result = new JsonArray();
        foreach (var v in en.Values ?? Enumerable.Empty<GraphQLEnumValueDefinition>())
        {
            var (deprecated, _) = Deprecation(v.Directives);
            result.Add(new JsonObject
            {
                ["name"] = v.Name.StringValue,
                ["description"] = Description(v.Description),
                ["isDeprecated"] = deprecated,
            });
        }
        return result;
    }

    /// <summary>
    /// A field's type as the nested <c>{ kind, name, ofType }</c> chain the
    /// spec uses.
    /// </summary>
    /// <remarks>
    /// The nesting is the whole point. <c>[String!]!</c> and <c>[String]</c>
    /// differ only in where the NON_NULL wrappers sit, and a client builds
    /// its required-argument markers out of exactly that.
    /// </remarks>
    private static JsonObject TypeRef(GraphQLType? type) => type switch
    {
        GraphQLNonNullType nn => new JsonObject
        {
            ["kind"] = "NON_NULL",
            ["name"] = null,
            ["ofType"] = TypeRef(nn.Type),
        },
        GraphQLListType list => new JsonObject
        {
            ["kind"] = "LIST",
            ["name"] = null,
            ["ofType"] = TypeRef(list.Type),
        },
        GraphQLNamedType named => new JsonObject
        {
            // Which kind a named type really is takes a second lookup, and
            // the client does not need it here: it resolves the name against
            // the type table, where the real kind sits. Only the wrappers
            // have to be right at this level.
            ["kind"] = "OBJECT",
            ["name"] = named.Name.StringValue,
            ["ofType"] = null,
        },
        _ => new JsonObject { ["kind"] = "SCALAR", ["name"] = "String", ["ofType"] = null },
    };

    private static (bool Deprecated, string? Reason) Deprecation(GraphQLDirectives? directives)
    {
        if (directives is null) return (false, null);
        foreach (var d in directives)
        {
            if (!string.Equals(d.Name.StringValue, "deprecated", StringComparison.Ordinal)) continue;
            string? reason = null;
            foreach (var a in d.Arguments ?? Enumerable.Empty<GraphQLArgument>())
            {
                if (string.Equals(a.Name.StringValue, "reason", StringComparison.Ordinal)
                    && a.Value is GraphQLStringValue s)
                {
                    reason = s.Value.ToString();
                }
            }
            return (true, reason);
        }
        return (false, null);
    }

    private static string? Description(GraphQLDescription? description)
        => description?.Value.ToString();
}
