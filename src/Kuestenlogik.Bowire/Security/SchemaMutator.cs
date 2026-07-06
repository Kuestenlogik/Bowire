// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security;

/// <summary>The type of a field the mutation engine knows how to attack.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "The members are deliberately named after the JSON-schema field types (integer / string / object) they model — the domain vocabulary a schema author uses, not incidental type-name collisions.")]
public enum FuzzFieldKind
{
    /// <summary>Integer / whole number.</summary>
    Integer,
    /// <summary>Floating-point number.</summary>
    Number,
    /// <summary>String.</summary>
    String,
    /// <summary>Boolean.</summary>
    Boolean,
    /// <summary>Enumeration with a fixed value set.</summary>
    Enum,
    /// <summary>Array / repeated field.</summary>
    Array,
    /// <summary>Object / message.</summary>
    Object,
}

/// <summary>What the engine knows about a field, extracted from the schema.</summary>
/// <param name="Kind">The field's declared type.</param>
/// <param name="Required">True when the field is required / non-optional.</param>
/// <param name="EnumValues">The allowed values when <see cref="Kind"/> is <see cref="FuzzFieldKind.Enum"/>.</param>
/// <param name="Format">Optional format hint (e.g. <c>email</c>, <c>uuid</c>, <c>date</c>) that adds a format-violation mutation.</param>
public sealed record FuzzFieldSpec(FuzzFieldKind Kind, bool Required = false, IReadOnlyList<string>? EnumValues = null, string? Format = null);

/// <summary>One targeted invalid input the engine produced for a field.</summary>
/// <param name="Label">Short human-readable name of the attack (e.g. <c>int32 overflow</c>).</param>
/// <param name="ValueJson">The raw JSON token to inject in place of the field's value — may be deliberately invalid JSON (e.g. <c>NaN</c>) to probe the parser.</param>
/// <param name="Expectation">What a correctly-validating server should do (used as the per-mutation report's expected-behaviour column).</param>
public sealed record SchemaMutation(string Label, string ValueJson, string Expectation);

/// <summary>
/// Schema-aware mutation engine (#175). Given a field's declared type it
/// produces targeted invalid inputs — the type-confusion, boundary, encoding,
/// enum-bypass, and structural mutations a schema-blind fuzzer misses. Output
/// is <b>seeded</b> (reproducible via <c>--seed</c>) and <b>budgeted</b> (capped
/// per field so a scan doesn't explode); the same (spec, seed, budget) always
/// yields the same mutation set.
/// </summary>
public static class SchemaMutator
{
    private const string OversizedString = "8192-char string";
    private const int OversizedArrayLen = 1000;

    /// <summary>
    /// Produce up to <paramref name="budget"/> targeted invalid inputs for the
    /// field, chosen deterministically from the candidate set by
    /// <paramref name="seed"/>.
    /// </summary>
    public static IReadOnlyList<SchemaMutation> Mutate(FuzzFieldSpec spec, int seed, int budget)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var all = Candidates(spec);

        // Required fields get a "must not be absent/null" case up front so it
        // survives budget trimming when the field is required.
        if (spec.Required && !all.Any(m => m.Label == "required omitted"))
            all.Insert(0, new SchemaMutation("required omitted", "__OMIT__", "reject: a required field must not be absent"));

        if (budget <= 0 || budget >= all.Count) return SeededShuffle(all, seed);
        return SeededShuffle(all, seed).Take(budget).ToList();
    }

    private static List<SchemaMutation> Candidates(FuzzFieldSpec spec) => spec.Kind switch
    {
        FuzzFieldKind.Integer => Integer(),
        FuzzFieldKind.Number => Number(),
        FuzzFieldKind.String => Strings(spec.Format),
        FuzzFieldKind.Boolean => Boolean(),
        FuzzFieldKind.Enum => Enum(spec.EnumValues),
        FuzzFieldKind.Array => Array(),
        FuzzFieldKind.Object => Object(),
        _ => [],
    };

    private static List<SchemaMutation> Integer() =>
    [
        new("zero boundary", "0", "accept or reject per business rule — watch for divide-by-zero / off-by-one"),
        new("negative", "-1", "reject when the field is unsigned / a count / an id"),
        new("int32 max", "2147483647", "accept if int64-backed; watch for silent int32 truncation"),
        new("int32 overflow", "2147483648", "reject or handle without wrap-around"),
        new("int64 max", "9223372036854775807", "accept or reject cleanly, never wrap"),
        new("int64 overflow", "9223372036854775808", "reject: exceeds int64"),
        new("float for integer", "1.5", "reject: non-integer value in an integer field"),
        new("string for integer", "\"123\"", "reject: type confusion (string in a numeric field)"),
        new("null", "null", "reject when the field is required"),
    ];

    private static List<SchemaMutation> Number() =>
    [
        new("zero", "0", "watch for divide-by-zero downstream"),
        new("NaN literal", "NaN", "reject: NaN is not valid JSON"),
        new("Infinity literal", "Infinity", "reject: Infinity is not valid JSON"),
        new("negative Infinity", "-Infinity", "reject: -Infinity is not valid JSON"),
        new("float overflow", "1e309", "reject or clamp — parses to Infinity in many stacks"),
        new("string for number", "\"1e999\"", "reject: type confusion"),
        new("null", "null", "reject when the field is required"),
    ];

    private static List<SchemaMutation> Strings(string? format)
    {
        var list = new List<SchemaMutation>
        {
            new("empty string", "\"\"", "reject when a non-empty value is required"),
            new("oversized", "\"" + new string('A', 8192) + "\"", $"reject: {OversizedString} exceeds any sane length cap"),
            new("embedded null byte", "\"a\\u0000b\"", "reject / sanitise: null byte can truncate downstream (C strings, logs)"),
            new("unicode RTL + BOM", "\"\\uFEFF\\u202Eevil\"", "sanitise: BOM + RTL override enable homograph / spoofing tricks"),
            new("encoded CRLF/null", "\"%00%0d%0a\"", "reject / decode-safely: CRLF injection into headers/logs"),
            new("number for string", "42", "reject: type confusion (number in a string field)"),
            new("null", "null", "reject when the field is required"),
        };
        if (!string.IsNullOrEmpty(format))
        {
            var bad = format.ToUpperInvariant() switch
            {
                "EMAIL" => "\"not-an-email\"",
                "UUID" => "\"xxxxxxxx-not-a-uuid\"",
                "DATE" or "DATE-TIME" => "\"2026-13-45T99:99:99Z\"",
                "URI" or "URL" => "\"ht!tp://\"",
                "IPV4" => "\"999.999.999.999\"",
                _ => "\"__format_violation__\"",
            };
            list.Add(new SchemaMutation($"{format} format violation", bad, $"reject: value does not satisfy the '{format}' format"));
        }
        return list;
    }

    private static List<SchemaMutation> Boolean() =>
    [
        new("string for boolean", "\"true\"", "reject: type confusion (string in a boolean field)"),
        new("number for boolean", "1", "reject: type confusion (number in a boolean field)"),
        new("invalid literal", "\"maybe\"", "reject: not a boolean"),
        new("null", "null", "reject when the field is required"),
    ];

    private static List<SchemaMutation> Enum(IReadOnlyList<string>? values)
    {
        var list = new List<SchemaMutation>
        {
            new("out-of-range value", "\"__bowire_not_in_enum__\"", "reject: value outside the declared enum set"),
            new("number for enum", "0", "reject: type confusion — unless numeric enum, watch for 0-index default"),
            new("null", "null", "reject when the field is required"),
        };
        if (values is { Count: > 0 })
        {
            // Flip each character's case to probe case-insensitive enum matching
            // (a bypass). Per-char flip avoids a whole-string ToLowerInvariant.
            var first = values[0];
            var variant = new string(first.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());
            if (!string.Equals(variant, first, StringComparison.Ordinal))
                list.Add(new SchemaMutation("case-variant bypass", "\"" + JsonEscape(variant) + "\"", "reject: enum matching must be case-sensitive to the declared casing"));
        }
        return list;
    }

    private static List<SchemaMutation> Array() =>
    [
        new("empty array", "[]", "reject when at least one element is required"),
        new("single null element", "[null]", "reject: null element in a typed array"),
        new("oversized array", "[" + string.Join(",", Enumerable.Repeat("0", OversizedArrayLen)) + "]", $"reject / paginate: {OversizedArrayLen}-element array is a resource-consumption vector (API4)"),
        new("duplicate elements", "[1,1]", "watch for dedup assumptions being violated"),
        new("object for array", "{}", "reject: type confusion (object in an array field)"),
        new("null", "null", "reject when the field is required"),
    ];

    private static List<SchemaMutation> Object() =>
    [
        new("empty object", "{}", "reject when required properties are missing"),
        new("unexpected property", "{\"__bowire_extra__\":1}", "reject / ignore: an unknown property must not be bound (mass assignment / API3)"),
        new("array for object", "[]", "reject: type confusion (array in an object field)"),
        new("null", "null", "reject when the field is required"),
    ];

    // Deterministic shuffle — same seed ⇒ same order, so --seed reproduces the
    // exact mutation subset chosen under a budget.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness",
        Justification = "A seeded, deterministic Fisher-Yates shuffle for reproducible mutation selection under a budget — the seed IS the feature (--seed reproduces the set). Not security-sensitive randomness.")]
    private static List<SchemaMutation> SeededShuffle(List<SchemaMutation> items, int seed)
    {
        var rng = new Random(seed);
        var copy = new List<SchemaMutation>(items);
        for (var i = copy.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }

    private static string JsonEscape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>Parse a <c>--type</c> CLI value into a <see cref="FuzzFieldKind"/>.</summary>
    public static FuzzFieldKind? ParseKind(string? value) => value?.ToUpperInvariant() switch
    {
        "INTEGER" or "INT" => FuzzFieldKind.Integer,
        "NUMBER" or "FLOAT" or "DOUBLE" => FuzzFieldKind.Number,
        "STRING" => FuzzFieldKind.String,
        "BOOLEAN" or "BOOL" => FuzzFieldKind.Boolean,
        "ENUM" => FuzzFieldKind.Enum,
        "ARRAY" => FuzzFieldKind.Array,
        "OBJECT" => FuzzFieldKind.Object,
        _ => null,
    };
}
