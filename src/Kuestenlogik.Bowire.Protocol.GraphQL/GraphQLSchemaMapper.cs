// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.GraphQL;

/// <summary>
/// Translates a GraphQL introspection result (the JSON shape returned by
/// <see cref="GraphQLIntrospectionQuery"/>) into Bowire's protocol-agnostic
/// model: each root operation type (Query / Mutation / Subscription) becomes
/// a <see cref="BowireServiceInfo"/>, each field on it becomes a
/// <see cref="BowireMethodInfo"/>, and each argument becomes a
/// <see cref="BowireFieldInfo"/>. Type references resolve through a
/// per-schema name → type lookup so input objects and enums render with the
/// same nested-form UI used for protobuf messages.
/// </summary>
internal sealed class GraphQLSchemaMapper
{
    private readonly Dictionary<string, JsonElement> _typesByName = new(StringComparer.Ordinal);

    public List<BowireServiceInfo> Map(JsonElement schemaResult, string serverUrl)
    {
        if (!schemaResult.TryGetProperty("__schema", out var schema))
            return [];

        if (schema.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array)
        {
            foreach (var type in types.EnumerateArray())
            {
                if (type.TryGetProperty("name", out var name) && name.GetString() is { } n)
                    _typesByName[n] = type;
            }
        }

        var services = new List<BowireServiceInfo>(3);

        AddRootService(services, schema, "queryType", "Query", "GraphQL queries", serverUrl, isSubscription: false);
        AddRootService(services, schema, "mutationType", "Mutation", "GraphQL mutations", serverUrl, isSubscription: false);
        AddRootService(services, schema, "subscriptionType", "Subscription", "GraphQL subscriptions", serverUrl, isSubscription: true);

        return services;
    }

    private void AddRootService(
        List<BowireServiceInfo> services,
        JsonElement schema,
        string rootProperty,
        string serviceName,
        string description,
        string serverUrl,
        bool isSubscription)
    {
        if (!schema.TryGetProperty(rootProperty, out var rootRef) || rootRef.ValueKind != JsonValueKind.Object)
            return;

        if (!rootRef.TryGetProperty("name", out var rootNameProp))
            return;

        var rootName = rootNameProp.GetString();
        if (string.IsNullOrEmpty(rootName) || !_typesByName.TryGetValue(rootName, out var rootType))
            return;

        if (!rootType.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            return;

        var methods = new List<BowireMethodInfo>();
        foreach (var field in fields.EnumerateArray())
        {
            methods.Add(MapField(field, serviceName, isSubscription));
        }

        if (methods.Count == 0)
            return;

        services.Add(new BowireServiceInfo(serviceName, "graphql", methods)
        {
            Source = "graphql",
            OriginUrl = serverUrl,
            Description = description
        });
    }

    private BowireMethodInfo MapField(JsonElement field, string serviceName, bool isSubscription)
    {
        var name = field.TryGetProperty("name", out var n) ? n.GetString() ?? "field" : "field";
        var description = field.TryGetProperty("description", out var d) ? d.GetString() : null;
        var deprecated = field.TryGetProperty("isDeprecated", out var dep) && dep.ValueKind == JsonValueKind.True;

        var args = new List<BowireFieldInfo>();
        if (field.TryGetProperty("args", out var argsArr) && argsArr.ValueKind == JsonValueKind.Array)
        {
            var i = 1;
            foreach (var arg in argsArr.EnumerateArray())
            {
                args.Add(MapArgument(arg, i++));
            }
        }

        var inputType = new BowireMessageInfo(name + "Variables", name + "Variables", args);

        // Walk the field's return type up to depth 3 so the JS layer has
        // a tree of selectable scalars / nested objects to render in the
        // selection-set picker. Cycles (a User with `friends: [User]` etc.)
        // are broken by the depth cap and the visited-set inside
        // ResolveOutputType.
        BowireMessageInfo outputType;
        if (field.TryGetProperty("type", out var fieldType))
        {
            outputType = ResolveOutputType(fieldType, name, depth: 0, new HashSet<string>(StringComparer.Ordinal))
                ?? new BowireMessageInfo("GraphQLResponse", "graphql.GraphQLResponse", []);
        }
        else
        {
            outputType = new BowireMessageInfo("GraphQLResponse", "graphql.GraphQLResponse", []);
        }

        return new BowireMethodInfo(
            Name: name,
            FullName: serviceName + "/" + name,
            ClientStreaming: false,
            ServerStreaming: isSubscription,
            InputType: inputType,
            OutputType: outputType,
            MethodType: isSubscription ? "ServerStreaming" : "Unary")
        {
            Summary = description,
            Description = description,
            Deprecated = deprecated
        };
    }

    private BowireFieldInfo MapArgument(JsonElement arg, int number)
    {
        var name = arg.TryGetProperty("name", out var n) ? n.GetString() ?? $"arg{number}" : $"arg{number}";
        var description = arg.TryGetProperty("description", out var d) ? d.GetString() : null;
        var defaultValue = arg.TryGetProperty("defaultValue", out var dv) && dv.ValueKind == JsonValueKind.String
            ? dv.GetString()
            : null;

        var (typeName, isRepeated, required, nested, enumValues) = arg.TryGetProperty("type", out var typeRef)
            ? ResolveType(typeRef, name)
            : ("string", false, false, null, null);

        return new BowireFieldInfo(
            Name: name,
            Number: number,
            Type: typeName,
            Label: required ? "required" : "optional",
            IsMap: false,
            IsRepeated: isRepeated,
            MessageType: nested,
            EnumValues: enumValues)
        {
            Required = required,
            Description = description,
            Source = "body",
            Example = defaultValue
        };
    }

    private (string Type, bool IsRepeated, bool Required, BowireMessageInfo? Nested, List<BowireEnumValue>? Enums)
        ResolveType(JsonElement typeRef, string contextName)
    {
        var required = false;
        var isRepeated = false;
        var current = typeRef;

        // Walk the wrapping NON_NULL / LIST chain. The GraphQL TypeRef is a
        // recursive linked list: NON_NULL → LIST → NON_NULL → NAMED, etc.
        while (current.ValueKind == JsonValueKind.Object)
        {
            var kind = current.TryGetProperty("kind", out var k) ? k.GetString() : null;
            if (kind == "NON_NULL")
            {
                required = true;
                current = current.GetProperty("ofType");
                continue;
            }
            if (kind == "LIST")
            {
                isRepeated = true;
                required = false; // reset — non_null applies to inner element
                current = current.GetProperty("ofType");
                continue;
            }
            break;
        }

        var namedKind = current.TryGetProperty("kind", out var kk) ? kk.GetString() : null;
        var namedName = current.TryGetProperty("name", out var nn) ? nn.GetString() : null;

        switch (namedKind)
        {
            case "SCALAR":
                return (MapScalar(namedName), isRepeated, required, null, null);

            case "ENUM":
                var enumValues = ResolveEnumValues(namedName);
                return ("string", isRepeated, required, null, enumValues);

            case "INPUT_OBJECT":
                var nested = ResolveInputObject(namedName, contextName);
                return ("message", isRepeated, required, nested, null);

            case "OBJECT":
            case "INTERFACE":
            case "UNION":
                // Output types — should not appear as argument types, but if they
                // do, surface as opaque "message" so the form still renders.
                return ("message", isRepeated, required, null, null);

            default:
                return ("string", isRepeated, required, null, null);
        }
    }

    private static string MapScalar(string? scalarName) => scalarName switch
    {
        "Int" => "int32",
        "Float" => "double",
        "Boolean" => "bool",
        "ID" => "string",
        "String" => "string",
        // Common custom scalars — fall back to string so the textbox renders
        _ => "string"
    };

    private List<BowireEnumValue>? ResolveEnumValues(string? enumName)
    {
        if (enumName is null || !_typesByName.TryGetValue(enumName, out var enumType))
            return null;

        if (!enumType.TryGetProperty("enumValues", out var values) || values.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<BowireEnumValue>();
        var i = 0;
        foreach (var v in values.EnumerateArray())
        {
            var name = v.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(name)) continue;
            result.Add(new BowireEnumValue(name, i++));
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Walk a GraphQL field type ref into a <see cref="BowireMessageInfo"/>
    /// for the JS selection-set picker. Strips NON_NULL / LIST wrappers,
    /// resolves the inner OBJECT / INTERFACE type by name, and recurses
    /// into its fields. Recursion is bounded both by an absolute depth
    /// cap (3 levels) and by a visited-set to break cycles
    /// (e.g. <c>User { friends: [User] }</c>).
    /// </summary>
    private BowireMessageInfo? ResolveOutputType(JsonElement typeRef, string contextName, int depth, HashSet<string> visited)
    {
        if (depth > 3) return null;

        // Unwrap NON_NULL / LIST so we get to the underlying named type.
        var current = typeRef;
        while (current.ValueKind == JsonValueKind.Object)
        {
            var k = current.TryGetProperty("kind", out var kk) ? kk.GetString() : null;
            if (k == "NON_NULL" || k == "LIST")
            {
                current = current.GetProperty("ofType");
                continue;
            }
            break;
        }

        var namedKind = current.TryGetProperty("kind", out var nk) ? nk.GetString() : null;
        var namedName = current.TryGetProperty("name", out var nn) ? nn.GetString() : null;

        // Only object / interface / union types have fields to select.
        // Scalars, enums, and unknown shapes return a placeholder so the
        // picker just sees an empty leaf.
        if (namedKind != "OBJECT" && namedKind != "INTERFACE")
            return null;

        if (namedName is null || !_typesByName.TryGetValue(namedName, out var typeDef))
            return null;

        // Cycle detection — bail if we've already seen this type at any
        // ancestor level on the current branch.
        if (!visited.Add(namedName))
            return new BowireMessageInfo(namedName, namedName, []);

        try
        {
            if (!typeDef.TryGetProperty("fields", out var fieldsArr) || fieldsArr.ValueKind != JsonValueKind.Array)
                return new BowireMessageInfo(namedName, namedName, []);

            var mapped = new List<BowireFieldInfo>();
            var i = 1;
            foreach (var f in fieldsArr.EnumerateArray())
            {
                var fname = f.TryGetProperty("name", out var fn) ? fn.GetString() ?? $"field{i}" : $"field{i}";
                var fdesc = f.TryGetProperty("description", out var fd) ? fd.GetString() : null;

                var (typeName, isRepeated, isNested, nested) = f.TryGetProperty("type", out var ft)
                    ? ResolveOutputFieldType(ft, fname, depth + 1, visited)
                    : ("string", false, false, null);

                mapped.Add(new BowireFieldInfo(
                    Name: fname,
                    Number: i++,
                    Type: isNested ? "message" : typeName,
                    Label: "optional",
                    IsMap: false,
                    IsRepeated: isRepeated,
                    MessageType: nested,
                    EnumValues: null)
                {
                    Description = fdesc,
                    Source = "selection"
                });
            }

            return new BowireMessageInfo(namedName, namedName, mapped);
        }
        finally
        {
            visited.Remove(namedName);
        }
    }

    /// <summary>
    /// Helper used by <see cref="ResolveOutputType"/> when walking a field's
    /// type. Returns the JS-friendly scalar name, the repeated flag, and a
    /// nested <see cref="BowireMessageInfo"/> when the field's return type
    /// is itself an object / interface that the picker should expand into.
    /// </summary>
    private (string Type, bool IsRepeated, bool IsNested, BowireMessageInfo? Nested) ResolveOutputFieldType(
        JsonElement typeRef, string contextName, int depth, HashSet<string> visited)
    {
        var isRepeated = false;
        var current = typeRef;

        while (current.ValueKind == JsonValueKind.Object)
        {
            var k = current.TryGetProperty("kind", out var kk) ? kk.GetString() : null;
            if (k == "NON_NULL")
            {
                current = current.GetProperty("ofType");
                continue;
            }
            if (k == "LIST")
            {
                isRepeated = true;
                current = current.GetProperty("ofType");
                continue;
            }
            break;
        }

        var namedKind = current.TryGetProperty("kind", out var kk2) ? kk2.GetString() : null;
        var namedName = current.TryGetProperty("name", out var nn2) ? nn2.GetString() : null;

        switch (namedKind)
        {
            case "SCALAR":
                return (MapScalar(namedName), isRepeated, false, null);

            case "ENUM":
                return ("string", isRepeated, false, null);

            case "OBJECT":
            case "INTERFACE":
                var nested = ResolveOutputType(current, contextName, depth, visited);
                return ("message", isRepeated, true, nested);

            case "UNION":
                // Unions don't have fields directly — pick the first
                // possibleType for the picker, since GraphQL fragment
                // selection isn't supported in this MVP.
                return ("message", isRepeated, true, null);

            default:
                return ("string", isRepeated, false, null);
        }
    }

    private BowireMessageInfo? ResolveInputObject(string? inputName, string contextName, int depth = 0)
    {
        if (inputName is null || depth > 4) return null;
        if (!_typesByName.TryGetValue(inputName, out var inputType)) return null;

        if (!inputType.TryGetProperty("inputFields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            return new BowireMessageInfo(inputName, inputName, []);

        var mapped = new List<BowireFieldInfo>();
        var i = 1;
        foreach (var f in fields.EnumerateArray())
        {
            var fname = f.TryGetProperty("name", out var fn) ? fn.GetString() ?? $"field{i}" : $"field{i}";
            var fdesc = f.TryGetProperty("description", out var fd) ? fd.GetString() : null;

            var (typeName, isRepeated, required, nested, enums) = f.TryGetProperty("type", out var ft)
                ? ResolveType(ft, fname)
                : ("string", false, false, null, null);

            mapped.Add(new BowireFieldInfo(
                Name: fname,
                Number: i++,
                Type: typeName,
                Label: required ? "required" : "optional",
                IsMap: false,
                IsRepeated: isRepeated,
                MessageType: nested,
                EnumValues: enums)
            {
                Required = required,
                Description = fdesc,
                Source = "body"
            });
        }

        return new BowireMessageInfo(inputName, inputName, mapped);
    }
}
