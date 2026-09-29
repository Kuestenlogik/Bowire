// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Scaffold;

/// <summary>
/// What a scaffold is generated from (#177): one entity, its fields, and the
/// protocol to serve it over. Written by the AI assistant from a sentence,
/// by <see cref="ScaffoldIntentParser"/> without a model, or by hand.
/// </summary>
/// <param name="Entity">The entity, singular — <c>User</c>.</param>
/// <param name="Protocol"><c>rest</c> (OpenAPI 3 + minimal API) or <c>grpc</c> (proto3 + Grpc.AspNetCore).</param>
/// <param name="Fields">The entity's fields besides <c>id</c>, which every entity gets.</param>
/// <param name="Service">The service name; defaults to the entity's plural — <c>Users</c>.</param>
/// <param name="BaseUrl">Where the stub listens; default <c>http://localhost:5000</c>.</param>
public sealed record ScaffoldSpec(
    [property: JsonPropertyName("entity")] string Entity,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("fields")] IReadOnlyList<ScaffoldField> Fields,
    [property: JsonPropertyName("service")] string? Service = null,
    [property: JsonPropertyName("baseUrl")] string? BaseUrl = null)
{
    /// <summary>The protocols a scaffold can target.</summary>
    public static readonly IReadOnlyList<string> Protocols = ["rest", "grpc"];

    /// <summary>The field types a spec may use.</summary>
    public static readonly IReadOnlyList<string> Types = ["string", "int", "long", "double", "bool", "datetime", "uuid"];

    /// <summary>The most fields one entity may carry.</summary>
    public const int MaxFields = 40;

    private static readonly Regex s_identifier = new("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// The spec with names brought into shape — entity PascalCase and
    /// singular, fields camelCase, type aliases resolved, a field called
    /// <c>id</c> dropped (every entity has one) — or the list of what cannot
    /// be fixed. Generation only ever sees a normalized spec.
    /// </summary>
    public (ScaffoldSpec? Spec, IReadOnlyList<string> Errors) Normalize()
    {
        var errors = new List<string>();
        var entity = Pascal(Entity ?? string.Empty);
        if (!s_identifier.IsMatch(entity)) errors.Add($"entity '{Entity}' is not a usable name (letters, digits, underscore; a letter first)");

        var protocol = ScaffoldNames.Lower((Protocol ?? string.Empty).Trim());
        if (!Protocols.Contains(protocol)) errors.Add($"protocol '{Protocol}' is not one of {string.Join(", ", Protocols)}");

        var service = string.IsNullOrWhiteSpace(Service) ? ScaffoldNames.Plural(entity) : Pascal(Service);
        if (!s_identifier.IsMatch(service)) errors.Add($"service '{Service}' is not a usable name");
        if (string.Equals(service, entity, StringComparison.Ordinal)) service += "Service";

        var baseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? "http://localhost:5000" : BaseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"baseUrl '{BaseUrl}' is not an absolute http(s) URL");
        }

        var fields = new List<ScaffoldField>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields ?? [])
        {
            if (field is null) continue;
            var name = Camel(field.Name ?? string.Empty);
            if (name.Length == 0) continue;
            if (!s_identifier.IsMatch(name))
            {
                errors.Add($"field '{field.Name}' is not a usable name");
                continue;
            }
            if (string.Equals(name, "id", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(name))
            {
                errors.Add($"field '{name}' appears twice");
                continue;
            }
            var type = ScaffoldNames.NormalizeType(field.Type);
            if (type is null)
            {
                errors.Add($"field '{name}' has type '{field.Type}', not one of {string.Join(", ", Types)}");
                continue;
            }
            fields.Add(new ScaffoldField(name, type, field.Required));
        }
        // A member named like its type does not compile, in C# or in the
        // generated protobuf classes.
        foreach (var f in fields.Where(f => string.Equals(Pascal(f.Name), entity, StringComparison.Ordinal)))
        {
            errors.Add($"field '{f.Name}' has the entity's own name");
        }
        if (fields.Count == 0) errors.Add("the entity needs at least one field besides id");
        if (fields.Count > MaxFields) errors.Add($"at most {MaxFields} fields");

        return errors.Count > 0
            ? (null, errors)
            : (new ScaffoldSpec(entity, protocol, fields, service, baseUrl), errors);
    }

    internal static string Pascal(string name)
    {
        var parts = SplitWords(name);
        var sb = new StringBuilder();
        foreach (var p in parts) sb.Append(char.ToUpperInvariant(p[0])).Append(p[1..]);
        return sb.ToString();
    }

    internal static string Camel(string name)
    {
        var pascal = Pascal(name);
        return pascal.Length == 0 ? pascal : char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    // "first name", "first_name", "first-name", "firstName" -> [first, Name]
    private static List<string> SplitWords(string name)
    {
        var words = new List<string>();
        foreach (var chunk in name.Trim().Split([' ', '_', '-', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            var start = 0;
            for (var i = 1; i < chunk.Length; i++)
            {
                if (char.IsUpper(chunk[i]) && char.IsLower(chunk[i - 1]))
                {
                    words.Add(chunk[start..i]);
                    start = i;
                }
            }
            words.Add(chunk[start..]);
        }
        return [.. words.Where(w => w.Length > 0)];
    }
}

/// <summary>One field of a <see cref="ScaffoldSpec"/>.</summary>
/// <param name="Name">The field name; camelCase on the wire.</param>
/// <param name="Type">One of <see cref="ScaffoldSpec.Types"/>.</param>
/// <param name="Required">Whether a create or update without it is refused.</param>
public sealed record ScaffoldField(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("required")] bool Required = false);

/// <summary>Naming helpers shared by the parser and the generator.</summary>
internal static class ScaffoldNames
{
    private static readonly Dictionary<string, string> s_typeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["string"] = "string", ["text"] = "string", ["str"] = "string", ["email"] = "string", ["url"] = "string",
        ["int"] = "int", ["integer"] = "int", ["int32"] = "int", ["number"] = "double",
        ["long"] = "long", ["int64"] = "long",
        ["double"] = "double", ["float"] = "double", ["decimal"] = "double",
        ["bool"] = "bool", ["boolean"] = "bool",
        ["datetime"] = "datetime", ["date"] = "datetime", ["timestamp"] = "datetime", ["date-time"] = "datetime",
        ["uuid"] = "uuid", ["guid"] = "uuid",
    };

    public static string? NormalizeType(string? type) =>
        string.IsNullOrWhiteSpace(type) ? "string" : s_typeAliases.GetValueOrDefault(type.Trim());

    /// <summary>English plural, good enough for entity names.</summary>
    public static string Plural(string word)
    {
        if (word.Length == 0) return word;
        if (word.EndsWith('y') && word.Length > 1 && !"aeiou".Contains(char.ToLowerInvariant(word[^2]), StringComparison.Ordinal))
            return word[..^1] + "ies";
        if (word.EndsWith('s') || word.EndsWith('x') || word.EndsWith('z')
            || word.EndsWith("ch", StringComparison.Ordinal) || word.EndsWith("sh", StringComparison.Ordinal))
            return word + "es";
        return word + "s";
    }

    /// <summary>English singular for the parser: <c>users</c> -> <c>user</c>.</summary>
    public static string Singular(string word)
    {
        if (word.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && word.Length > 3) return word[..^3] + "y";
        if (word.EndsWith("sses", StringComparison.OrdinalIgnoreCase) || word.EndsWith("xes", StringComparison.OrdinalIgnoreCase)
            || word.EndsWith("ches", StringComparison.OrdinalIgnoreCase) || word.EndsWith("shes", StringComparison.OrdinalIgnoreCase))
            return word[..^2];
        if (word.EndsWith('s') && !word.EndsWith("ss", StringComparison.OrdinalIgnoreCase) && word.Length > 2) return word[..^1];
        return word;
    }

    // Identifiers, protocol ids and routes are lower-case by contract;
    // per character, so no culture and no CA1308.
    public static string Lower(string value) => string.Create(value.Length, value, static (span, v) =>
    {
        for (var i = 0; i < v.Length; i++) span[i] = char.ToLowerInvariant(v[i]);
    });

    public static string Snake(string camel)
    {
        var sb = new StringBuilder();
        foreach (var c in camel)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append('_');
            sb.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
