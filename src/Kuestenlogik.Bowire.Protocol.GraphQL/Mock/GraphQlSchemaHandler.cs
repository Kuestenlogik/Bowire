// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using System.Text;
using System.Text.Json.Nodes;
using GraphQLParser;
using GraphQLParser.AST;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.GraphQL.Mock;

/// <summary>
/// GraphQL schema-only mock handler. Loads an SDL file once at
/// startup, indexes its type definitions, and answers every
/// <c>POST /graphql</c> request by parsing the incoming query,
/// walking the schema for each selected field, and emitting a
/// sample-valued JSON response that matches the selection set.
/// </summary>
/// <remarks>
/// <para>
/// Unlike OpenAPI / gRPC schema-only (which pre-generate one sample
/// per operation at startup and feed the normal replay pipeline),
/// GraphQL responses are selection-set-dependent: <c>{ user { name
/// } }</c> and <c>{ user { name, email } }</c> against the same
/// schema yield different response shapes. That rules out the
/// synthetic-recording approach and calls for a live handler.
/// </para>
/// <para>
/// Scope of this first slice:
/// </para>
/// <list type="bullet">
///   <item>Query operations only (Mutations / Subscriptions parsed
///   but emit a generic error).</item>
///   <item>Scalars, lists, nullable wrappers, and object types in
///   the selection set.</item>
///   <item>Aliases honoured; arguments ignored for sample generation
///   (we don't resolve filter/paging semantics).</item>
///   <item>Interfaces / unions skipped — the mock can't guess which
///   concrete type to emit without runtime resolvers. Fields whose
///   type is an interface or union return <c>null</c>.</item>
/// </list>
/// </remarks>
public sealed class GraphQlSchemaHandler
{
    private readonly FrozenDictionary<string, TypeInfo> _types;
    private readonly string? _queryRootTypeName;
    private readonly ILogger _logger;

    private GraphQlSchemaHandler(
        FrozenDictionary<string, TypeInfo> types,
        string? queryRootTypeName,
        ILogger logger)
    {
        _types = types;
        _queryRootTypeName = queryRootTypeName;
        _logger = logger;
    }

    /// <summary>Parse the given SDL file and build a handler ready for request dispatch.</summary>
    public static async Task<GraphQlSchemaHandler> LoadAsync(string path, ILogger logger, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"GraphQL schema file not found: {path}", path);

        var sdl = await File.ReadAllTextAsync(path, ct);
        var doc = Parser.Parse(sdl);

        var types = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);
        string? queryRoot = "Query"; // default per spec

        foreach (var def in doc.Definitions)
        {
            switch (def)
            {
                case GraphQLObjectTypeDefinition obj:
                    types[obj.Name.StringValue] = BuildTypeInfo(obj);
                    break;
                case GraphQLInterfaceTypeDefinition iface:
                    // Track interface names so we know to skip-with-null,
                    // but don't index fields — can't pick a concrete type.
                    types[iface.Name.StringValue] = new TypeInfo(iface.Name.StringValue, IsInterface: true, FrozenDictionary<string, FieldInfo>.Empty, EnumValues: null);
                    break;
                case GraphQLEnumTypeDefinition en:
                    var vals = en.Values?.Select(v => v.Name.StringValue).ToArray() ?? [];
                    types[en.Name.StringValue] = new TypeInfo(en.Name.StringValue, IsInterface: false, FrozenDictionary<string, FieldInfo>.Empty, EnumValues: vals);
                    break;
                case GraphQLSchemaDefinition schema:
                    // Schema may rename the query root.
                    foreach (var op in schema.OperationTypes ?? Enumerable.Empty<GraphQLRootOperationTypeDefinition>())
                    {
                        if (op.Operation == OperationType.Query)
                            queryRoot = op.Type?.Name.StringValue;
                    }
                    break;
            }
        }

        return new GraphQlSchemaHandler(
            types.ToFrozenDictionary(StringComparer.Ordinal),
            queryRoot,
            logger);
    }

    /// <summary>
    /// Try to handle the request. Returns <c>true</c> when the
    /// request targeted <c>POST /graphql</c> and a response was
    /// written; <c>false</c> otherwise so the caller can pass through
    /// to the next middleware.
    /// </summary>
    public async Task<bool> TryHandleAsync(HttpContext ctx, CancellationToken ct)
    {
        if (!string.Equals(ctx.Request.Method, "POST", StringComparison.Ordinal)) return false;
        if (!string.Equals(ctx.Request.Path.Value, "/graphql", StringComparison.Ordinal)) return false;

        string body;
        using (var reader = new StreamReader(ctx.Request.Body))
        {
            body = await reader.ReadToEndAsync(ct);
        }

        string? query;
        try
        {
            using var parsed = System.Text.Json.JsonDocument.Parse(body);
            query = parsed.RootElement.TryGetProperty("query", out var q) ? q.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            await WriteJsonAsync(ctx, """{"errors":[{"message":"Request body is not valid JSON"}]}""", ct);
            return true;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            await WriteJsonAsync(ctx, """{"errors":[{"message":"Request body is missing a 'query' field"}]}""", ct);
            return true;
        }

        GraphQLDocument queryDoc;
        try
        {
            queryDoc = Parser.Parse(query);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(ctx,
                $$"""{"errors":[{"message":"Query parse error: {{EscapeJson(ex.Message)}}"}]}""", ct);
            return true;
        }

        var operation = queryDoc.Definitions.OfType<GraphQLOperationDefinition>().FirstOrDefault();
        if (operation is null)
        {
            await WriteJsonAsync(ctx,
                """{"errors":[{"message":"No operation found in the document"}]}""", ct);
            return true;
        }

        if (operation.Operation != OperationType.Query)
        {
            await WriteJsonAsync(ctx,
                """{"errors":[{"message":"This schema-only mock supports Query operations only."}]}""", ct);
            return true;
        }

        if (_queryRootTypeName is null || !_types.TryGetValue(_queryRootTypeName, out var queryRoot))
        {
            await WriteJsonAsync(ctx,
                """{"errors":[{"message":"Schema has no Query root type."}]}""", ct);
            return true;
        }

        var data = RenderSelectionSet(operation.SelectionSet, queryRoot, depth: 0);

        var response = new JsonObject { ["data"] = data };
        await WriteJsonAsync(ctx, response.ToJsonString(), ct);
        _logger.LogInformation(
            "graphql-schema(operation={Name}, rootFields={Fields})",
            operation.Name?.StringValue ?? "<anonymous>",
            operation.SelectionSet.Selections.Count);
        return true;
    }

    private JsonObject? RenderSelectionSet(GraphQLSelectionSet? selectionSet, TypeInfo parent, int depth)
    {
        if (selectionSet is null || depth >= MaxDepth) return null;
        var result = new JsonObject();

        foreach (var selection in selectionSet.Selections)
        {
            if (selection is not GraphQLField field) continue; // fragment spreads / inline fragments not supported in v1
            var fieldName = field.Name.StringValue;
            var responseKey = field.Alias?.Name.StringValue ?? fieldName;

            // __typename is a convention — answer with the parent type.
            if (fieldName == "__typename")
            {
                result[responseKey] = parent.Name;
                continue;
            }

            if (!parent.Fields.TryGetValue(fieldName, out var fieldInfo))
            {
                // Schema doesn't have this field on the parent type —
                // emit null so the client doesn't read undefined.
                result[responseKey] = null;
                continue;
            }

            result[responseKey] = RenderField(fieldInfo, field.SelectionSet, depth + 1);
        }
        return result;
    }

    private JsonNode? RenderField(FieldInfo field, GraphQLSelectionSet? subSelection, int depth)
    {
        if (depth >= MaxDepth) return null;

        if (field.IsList)
        {
            var arr = new JsonArray();
            // Three list items — same convention as the OpenAPI
            // sample generator.
            for (var i = 0; i < 3; i++)
            {
                arr.Add(RenderScalarOrObject(field, subSelection, depth));
            }
            return arr;
        }

        return RenderScalarOrObject(field, subSelection, depth);
    }

    private JsonNode? RenderScalarOrObject(FieldInfo field, GraphQLSelectionSet? subSelection, int depth)
    {
        // Resolve the named type of the field. Might be a scalar,
        // enum, or an object type we have in our index.
        if (_types.TryGetValue(field.NamedType, out var typeInfo))
        {
            if (typeInfo.IsInterface)
            {
                // Can't pick a concrete implementor without runtime
                // knowledge — return null so downstream clients see
                // an explicitly-absent value.
                return null;
            }
            if (typeInfo.EnumValues is { Length: > 0 })
            {
                return typeInfo.EnumValues[0]; // first enum value
            }
            // Object type — recurse into selection set.
            return RenderSelectionSet(subSelection, typeInfo, depth);
        }

        // Built-in scalar (or unknown scalar): emit a type-appropriate
        // placeholder.
        return ScalarSample(field.NamedType);
    }

    private static JsonValue ScalarSample(string typeName) => typeName switch
    {
        "Int" => JsonValue.Create(1),
        "Float" => JsonValue.Create(1.5),
        "Boolean" => JsonValue.Create(true),
        "ID" => JsonValue.Create("id-1"),
        "String" => JsonValue.Create("sample"),
        // Common custom scalars — best-effort, client libraries
        // tolerate unexpected string values for these.
        "DateTime" or "Date" => JsonValue.Create("2026-01-01T00:00:00Z"),
        "UUID" or "Guid" => JsonValue.Create("00000000-0000-0000-0000-000000000000"),
        "Email" => JsonValue.Create("sample@example.com"),
        "URL" or "URI" => JsonValue.Create("https://example.com"),
        "JSON" => JsonValue.Create("{}"),
        _ => JsonValue.Create("sample")
    };

    private static async Task WriteJsonAsync(HttpContext ctx, string json, CancellationToken ct)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        await ctx.Response.Body.WriteAsync(bytes, ct);
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal)
         .Replace("\"", "\\\"", StringComparison.Ordinal)
         .Replace("\n", "\\n", StringComparison.Ordinal)
         .Replace("\r", "\\r", StringComparison.Ordinal);

    private static TypeInfo BuildTypeInfo(GraphQLObjectTypeDefinition obj)
    {
        var fields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        if (obj.Fields is not null)
        {
            foreach (var f in obj.Fields)
            {
                var (namedType, isList) = FlattenType(f.Type);
                fields[f.Name.StringValue] = new FieldInfo(f.Name.StringValue, namedType, isList);
            }
        }
        return new TypeInfo(obj.Name.StringValue, IsInterface: false, fields.ToFrozenDictionary(StringComparer.Ordinal), EnumValues: null);
    }

    // Strip the NonNullType / ListType wrappers off a GraphQL type
    // reference, returning the underlying named type + whether it was
    // wrapped in a list at any level.
    // internal (not private) so the test suite can hand-craft inputs that
    // hit the defensive default-arm — the SDL parser only ever emits
    // NamedType / ListType / NonNullType for valid input, so the fallback
    // is otherwise unreachable.
    internal static (string NamedType, bool IsList) FlattenType(GraphQLType? type)
    {
        var isList = false;
        var current = type;
        while (true)
        {
            switch (current)
            {
                case GraphQLNamedType named:
                    return (named.Name.StringValue, isList);
                case GraphQLListType list:
                    isList = true;
                    current = list.Type;
                    break;
                case GraphQLNonNullType nonNull:
                    current = nonNull.Type;
                    break;
                default:
                    return ("String", isList); // shouldn't happen for valid SDL
            }
        }
    }

    private const int MaxDepth = 8;

    private sealed record TypeInfo(
        string Name,
        bool IsInterface,
        FrozenDictionary<string, FieldInfo> Fields,
        string[]? EnumValues);

    private sealed record FieldInfo(string Name, string NamedType, bool IsList);
}
