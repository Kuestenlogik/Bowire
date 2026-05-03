// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Kuestenlogik.Bowire.Protocol.Rest.Mock;

/// <summary>
/// Phase 3d: generates a plausible JSON sample from an OpenAPI response
/// schema so the mock can replay operations that were never recorded —
/// the mock becomes a schema-first stub server without any hand-written
/// responses.
/// </summary>
/// <remarks>
/// <para>
/// Generation is type-aware:
/// </para>
/// <list type="bullet">
/// <item><c>integer</c> → <c>1</c> (or <c>minimum</c>/<c>example</c>/<c>default</c> when set)</item>
/// <item><c>number</c> → <c>1.5</c></item>
/// <item><c>string</c> → <c>"sample"</c>, or a format-aware placeholder (<c>date-time</c>, <c>uuid</c>, <c>email</c>, <c>uri</c>, <c>byte</c>)</item>
/// <item><c>boolean</c> → <c>true</c></item>
/// <item><c>array</c> → 3 items of the inner schema</item>
/// <item><c>object</c> → walks properties recursively, emitting the required set plus every defined property</item>
/// <item>enums → the first enum value</item>
/// </list>
/// <para>
/// Any <c>example</c> or <c>default</c> baked into the schema wins over
/// the generator's guess. Recursion depth is capped (cyclic <c>$ref</c>
/// trees hit the cap and get a stub object).
/// </para>
/// </remarks>
public static class OpenApiSampleGenerator
{
    private const int MaxDepth = 8;

    /// <summary>
    /// Generate a compact JSON string representing a plausible instance
    /// of <paramref name="schema"/>. Returns <c>"null"</c> when the
    /// schema is missing.
    /// </summary>
    public static string Generate(IOpenApiSchema? schema)
    {
        var node = BuildNode(schema, depth: 0);
        return node is null ? "null" : node.ToJsonString();
    }

    private static JsonNode? BuildNode(IOpenApiSchema? schema, int depth)
    {
        if (schema is null || depth >= MaxDepth)
            return JsonValue.Create("sample");

        // Explicit example / default on the schema wins over the
        // type-driven guess. OpenAPI lets both live as JsonNode values.
        if (schema.Example is JsonNode example) return example.DeepClone();
        if (schema.Default is JsonNode def) return def.DeepClone();

        // Enum: always pick the first value. Clone so the caller can't
        // mutate the schema-owned node.
        if (schema.Enum is { Count: > 0 } enumValues)
        {
            foreach (var ev in enumValues)
            {
                if (ev is not null) return ev.DeepClone();
            }
        }

        var type = schema.Type ?? JsonSchemaType.String;
        var nonNull = type & ~JsonSchemaType.Null;

        if ((nonNull & JsonSchemaType.Array) != 0)
        {
            var arr = new JsonArray();
            var itemNode = BuildNode(schema.Items, depth + 1) ?? JsonValue.Create("sample");
            // Three items — one item feels accidental, three feels
            // "yes, this really is an array". Cap below any explicit
            // MinItems/MaxItems when the schema constrains the length.
            var count = 3;
            if (schema.MinItems is int min && count < min) count = min;
            if (schema.MaxItems is int max && count > max) count = max;
            for (var i = 0; i < count; i++)
            {
                arr.Add(itemNode.DeepClone());
            }
            return arr;
        }

        if ((nonNull & JsonSchemaType.Object) != 0)
        {
            return BuildObject(schema, depth);
        }

        if ((nonNull & JsonSchemaType.Integer) != 0)
        {
            return BuildInteger(schema);
        }

        if ((nonNull & JsonSchemaType.Number) != 0)
        {
            return BuildNumber(schema);
        }

        if ((nonNull & JsonSchemaType.Boolean) != 0)
        {
            return JsonValue.Create(true);
        }

        // Default: string. Every remaining shape (including
        // format-only, typeless schemas) lands here.
        return BuildString(schema);
    }

    private static JsonObject BuildObject(IOpenApiSchema schema, int depth)
    {
        var obj = new JsonObject();
        if (schema.Properties is { Count: > 0 } props)
        {
            foreach (var (name, propSchema) in props)
            {
                obj[name] = BuildNode(propSchema, depth + 1);
            }
        }
        return obj;
    }

    private static JsonValue BuildInteger(IOpenApiSchema schema)
    {
        // Prefer minimum when set (honours constraints); otherwise 1.
        // Long-format widens but the value stays small so generated
        // samples remain human-readable.
        long value = 1;
        if (long.TryParse(schema.Minimum, System.Globalization.CultureInfo.InvariantCulture, out var min))
        {
            value = Math.Max(value, min);
        }
        return JsonValue.Create(value);
    }

    private static JsonValue BuildNumber(IOpenApiSchema schema)
    {
        double value = 1.5;
        if (double.TryParse(schema.Minimum, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var min))
        {
            value = Math.Max(value, min);
        }
        return JsonValue.Create(value);
    }

    private static JsonValue BuildString(IOpenApiSchema schema)
    {
        var format = schema.Format;
        if (string.IsNullOrEmpty(format)) return JsonValue.Create("sample");

        // Common OpenAPI string formats get purpose-built placeholders
        // so downstream validators (JSON Schema, Pydantic, Zod) actually
        // accept the generated sample. Unknown formats fall back to
        // "sample" — better to ship a string than to crash.
        return format.ToUpperInvariant() switch
        {
            "DATE-TIME" => JsonValue.Create("2026-01-01T00:00:00Z"),
            "DATE" => JsonValue.Create("2026-01-01"),
            "TIME" => JsonValue.Create("00:00:00"),
            "UUID" => JsonValue.Create("00000000-0000-0000-0000-000000000000"),
            "EMAIL" => JsonValue.Create("sample@example.com"),
            "URI" or "URL" => JsonValue.Create("https://example.com"),
            "HOSTNAME" => JsonValue.Create("example.com"),
            "IPV4" => JsonValue.Create("127.0.0.1"),
            "IPV6" => JsonValue.Create("::1"),
            "BYTE" => JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes("sample"))),
            "BINARY" => JsonValue.Create("sample"),
            "PASSWORD" => JsonValue.Create("sample"),
            _ => JsonValue.Create("sample")
        };
    }
}
