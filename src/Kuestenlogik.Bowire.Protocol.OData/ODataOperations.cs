// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Microsoft.OData.Edm;

namespace Kuestenlogik.Bowire.Protocol.OData;

/// <summary>
/// EDM functions and actions as Bowire methods (#529): how discovery lists
/// them, and how a call to one becomes an OData request.
/// </summary>
/// <remarks>
/// <para>
/// A method's full name says how to call it, so a call works without a
/// discovery in the same process (a flow, a recording):
/// <c>odata/{set}/FUNCTION/{Ns.Name}</c> on one entity,
/// <c>odata/{set}/FUNCTION_SET/{Ns.Name}</c> on the entity set, the same with
/// <c>ACTION</c>, and <c>odata/{container}/FUNCTION_IMPORT/{Name}</c> or
/// <c>ACTION_IMPORT</c> for an unbound operation import.
/// </para>
/// <para>
/// A function is a GET with its parameters written into the URL segment,
/// <c>Ns.Name(p=v,…)</c>; an action is a POST with its parameters as the
/// JSON body. An entity-bound operation addresses its entity by key first,
/// <c>{set}({key})/Ns.Name(…)</c>.
/// </para>
/// </remarks>
internal static class ODataOperations
{
    public const string Function = "FUNCTION";
    public const string FunctionOnSet = "FUNCTION_SET";
    public const string Action = "ACTION";
    public const string ActionOnSet = "ACTION_SET";
    public const string FunctionImport = "FUNCTION_IMPORT";
    public const string ActionImport = "ACTION_IMPORT";

    /// <summary>How a parameter value is written into a function's URL segment.</summary>
    public enum LiteralKind
    {
        /// <summary>Quoted: <c>'text'</c>.</summary>
        Quoted,
        /// <summary>As written: numbers, booleans, GUIDs, dates.</summary>
        Bare,
        /// <summary>A structured value, passed as a JSON parameter alias.</summary>
        Alias,
    }

    /// <summary>What a call needs to know about an operation's parameters, learned at discovery.</summary>
    public sealed record Signature(IReadOnlyDictionary<string, LiteralKind> Parameters);

    public static bool IsOperationVerb(string verb)
        => verb is Function or FunctionOnSet or Action or ActionOnSet or FunctionImport or ActionImport;

    /// <summary>The methods for the operations bound to <paramref name="entityType"/> or to a collection of it.</summary>
    public static IEnumerable<(BowireMethodInfo Method, Signature Signature)> BoundTo(
        IEdmModel model, IEdmEntityType entityType, string entitySetName, BowireMessageInfo keyInput)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in model.SchemaElements.OfType<IEdmOperation>().Where(o => o.IsBound))
        {
            var binding = operation.Parameters.FirstOrDefault()?.Type;
            if (binding is null) continue;

            bool onEntity;
            if (binding.IsEntity() && IsSameOrBase(binding.AsEntity().EntityDefinition(), entityType)) onEntity = true;
            else if (binding.IsCollection()
                && binding.AsCollection().ElementType() is { } element && element.IsEntity()
                && IsSameOrBase(element.AsEntity().EntityDefinition(), entityType)) onEntity = false;
            else continue;

            var isAction = operation is IEdmAction;
            var verb = (isAction, onEntity) switch
            {
                (false, true) => Function,
                (false, false) => FunctionOnSet,
                (true, true) => Action,
                (true, false) => ActionOnSet,
            };
            var parameters = operation.Parameters.Skip(1).ToList();
            var fields = new List<BowireFieldInfo>();
            if (onEntity) fields.AddRange(keyInput.Fields);
            fields.AddRange(FieldsOf(parameters, fields.Count + 1));

            var qualified = operation.FullName();
            var display = Unique(taken, operation.Name, parameters);
            var target = onEntity ? $"/{entitySetName}({{key}})/{qualified}" : $"/{entitySetName}/{qualified}";
            var method = new BowireMethodInfo(
                display, $"odata/{entitySetName}/{verb}/{qualified}", false, false,
                new BowireMessageInfo(operation.Name + "Input", qualified + "Input", fields),
                OutputOf(operation), "Unary")
            {
                HttpMethod = isAction ? "POST" : "GET",
                HttpPath = isAction ? target : target + "(…)",
                Summary = (isAction ? "Action" : "Function") + (onEntity ? $" on one {entityType.Name}" : $" on {entitySetName}"),
                Description = Describe(operation, parameters),
            };
            yield return (method, SignatureOf(parameters));
        }
    }

    /// <summary>The unbound function and action imports of the container, as one service's methods.</summary>
    public static IEnumerable<(BowireMethodInfo Method, Signature Signature)> Imports(IEdmEntityContainer container)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var import in container.OperationImports())
        {
            var operation = import.Operation;
            var isAction = import.IsActionImport();
            var verb = isAction ? ActionImport : FunctionImport;
            var parameters = operation.Parameters.ToList();
            var display = Unique(taken, import.Name, parameters);
            var method = new BowireMethodInfo(
                display, $"odata/{container.Name}/{verb}/{import.Name}", false, false,
                new BowireMessageInfo(import.Name + "Input", operation.FullName() + "Input", FieldsOf(parameters, 1)),
                OutputOf(operation), "Unary")
            {
                HttpMethod = isAction ? "POST" : "GET",
                HttpPath = isAction ? $"/{import.Name}" : $"/{import.Name}(…)",
                Summary = (isAction ? "Action import " : "Function import ") + import.Name,
                Description = Describe(operation, parameters),
            };
            yield return (method, SignatureOf(parameters));
        }
    }

    /// <summary>
    /// The request a call to an operation makes: the URL and, for an action,
    /// the body. <paramref name="keyName"/> is the entity's key property for
    /// an entity-bound operation; a <c>key</c> property is taken as well.
    /// </summary>
    public static (Uri Url, string? Body) Request(
        string baseUrl, string setOrContainer, string verb, string operationName, string payload,
        string? keyName, bool isStringKey, Signature? signature, Func<string, bool, string> formatKey)
    {
        var values = new List<(string Name, JsonElement Value)>();
        string? key = null;
        using (var doc = ParseObject(payload))
        {
            if (doc is not null)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name.StartsWith('$')) continue;
                    var isKey = string.Equals(prop.Name, "key", StringComparison.Ordinal)
                        || (keyName is not null && string.Equals(prop.Name, keyName, StringComparison.Ordinal));
                    if (isKey && verb is Function or Action)
                    {
                        key ??= prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText();
                        continue;
                    }
                    values.Add((prop.Name, prop.Value.Clone()));
                }
            }
        }

        var path = verb switch
        {
            Function or Action => $"{baseUrl}/{setOrContainer}({formatKey(key ?? string.Empty, isStringKey)})/{operationName}",
            FunctionOnSet or ActionOnSet => $"{baseUrl}/{setOrContainer}/{operationName}",
            _ => $"{baseUrl}/{operationName}",
        };

        if (verb is Action or ActionOnSet or ActionImport)
        {
            return (new Uri(path), BodyOf(values));
        }

        var segment = new StringBuilder();
        var aliases = new List<string>();
        foreach (var (name, value) in values)
        {
            if (segment.Length > 0) segment.Append(',');
            var kind = signature is not null && signature.Parameters.TryGetValue(name, out var declared)
                ? declared
                : KindOf(value);
            if (kind == LiteralKind.Alias || value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                segment.Append(name).Append("=@").Append(name);
                aliases.Add("@" + name + "=" + Uri.EscapeDataString(value.GetRawText()));
                continue;
            }
            segment.Append(name).Append('=').Append(Uri.EscapeDataString(Literal(value, kind)));
        }
        var url = path + "(" + segment + ")" + (aliases.Count == 0 ? "" : "?" + string.Join("&", aliases));
        return (new Uri(url), null);
    }

    // ---- helpers ----

    private static bool IsSameOrBase(IEdmEntityType bound, IEdmEntityType entityType)
    {
        for (IEdmStructuredType? t = entityType; t is not null; t = t.BaseType)
        {
            if (ReferenceEquals(t, bound) || string.Equals(((IEdmSchemaElement)t).FullName(), bound.FullName(), StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Overloads share a name: the second one is told apart by its parameters.
    private static string Unique(HashSet<string> taken, string name, IReadOnlyList<IEdmOperationParameter> parameters)
    {
        if (taken.Add(name)) return name;
        var withParams = $"{name}({string.Join(",", parameters.Select(p => p.Name))})";
        taken.Add(withParams);
        return withParams;
    }

    private static List<BowireFieldInfo> FieldsOf(IReadOnlyList<IEdmOperationParameter> parameters, int firstNumber)
    {
        var fields = new List<BowireFieldInfo>();
        var n = firstNumber;
        foreach (var p in parameters)
        {
            fields.Add(new BowireFieldInfo(p.Name, n++, TypeName(p.Type), "LABEL_OPTIONAL", false, false, null, null)
            {
                Required = p is not IEdmOptionalParameter && !p.Type.IsNullable,
                Description = $"Parameter, type: {p.Type.FullName()}",
            });
        }
        return fields;
    }

    private static Signature SignatureOf(IReadOnlyList<IEdmOperationParameter> parameters)
        => new(parameters.ToDictionary(p => p.Name, p => KindOf(p.Type), StringComparer.Ordinal));

    private static BowireMessageInfo OutputOf(IEdmOperation operation)
        => new("Result", operation.FullName() + "Result",
            ReturnTypeOf(operation) is not { } returns ? [] :
            [new BowireFieldInfo("value", 1, TypeName(returns), "LABEL_OPTIONAL", false, false, null, null)
                { Description = $"Returns {returns.FullName()}" }]);

    private static string Describe(IEdmOperation operation, List<IEdmOperationParameter> parameters)
        => $"{(operation is IEdmAction ? "Action" : "Function")} {operation.FullName()}"
           + (parameters.Count == 0 ? "" : $"({string.Join(", ", parameters.Select(p => $"{p.Name}: {p.Type.FullName()}"))})")
           + (ReturnTypeOf(operation) is { } returns ? $" → {returns.FullName()}" : "");

    private static IEdmTypeReference? ReturnTypeOf(IEdmOperation operation) => operation.GetReturn()?.Type;

    private static string TypeName(IEdmTypeReference type)
    {
        if (type.IsString()) return "string";
        if (type.IsInt16() || type.IsInt32() || type.IsInt64() || type.IsByte() || type.IsSByte()) return "int64";
        if (type.IsDouble() || type.IsSingle() || type.IsDecimal()) return "double";
        if (type.IsBoolean()) return "bool";
        if (type.IsStructured() || type.IsCollection()) return "message";
        return "string";
    }

    private static LiteralKind KindOf(IEdmTypeReference type)
    {
        if (type.IsString()) return LiteralKind.Quoted;
        if (type.IsEnum()) return LiteralKind.Quoted;
        if (type.IsStructured() || type.IsCollection()) return LiteralKind.Alias;
        return LiteralKind.Bare;
    }

    // Without a discovered signature: a JSON string is text, everything
    // else is written as JSON spells it.
    private static LiteralKind KindOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => LiteralKind.Quoted,
        JsonValueKind.Object or JsonValueKind.Array => LiteralKind.Alias,
        _ => LiteralKind.Bare,
    };

    private static string Literal(JsonElement value, LiteralKind kind)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "null";
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        return kind == LiteralKind.Quoted ? "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'" : text;
    }

    private static string BodyOf(List<(string Name, JsonElement Value)> values)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in values)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static JsonDocument? ParseObject(string payload)
    {
        try
        {
            var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
        }
        catch (JsonException)
        {
            // not a JSON object: no parameters
        }
        return null;
    }
}
