// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Linting.Rules;

/// <summary>A naming convention a name can be read as following.</summary>
internal enum NamingStyle
{
    /// <summary>Not an identifier, or one that fits several conventions at once.</summary>
    None,
    PascalCase,
    CamelCase,
    SnakeCase,
    KebabCase,
    ScreamingSnakeCase,
}

/// <summary>
/// Reads which convention a name follows, and finds the outliers among many.
/// </summary>
/// <remarks>
/// <para>
/// The naming rules (#583) judge consistency, not a style. A fixed convention
/// is protocol-sensitive: gRPC methods are PascalCase by convention, REST
/// operation ids camelCase, protobuf fields snake_case — a rule that demanded
/// one of them would be wrong for the others, which is why the first attempt
/// was deferred in #189. What is wrong everywhere is a surface that mixes
/// them, and that needs no opinion about which one is right: the majority of
/// the service's own names says so.
/// </para>
/// <para>
/// A name that fits several conventions does not vote. <c>status</c> is valid
/// camelCase, snake_case and kebab-case; <c>ID</c> is PascalCase and
/// SCREAMING_CASE. Counting them would let a surface of one-word names read
/// as whatever convention the few longer names happen to use. A name that is
/// not an identifier at all — <c>GET_/pets/{id}</c>, synthesised for a REST
/// operation without an operationId — does not vote either.
/// </para>
/// </remarks>
internal static class NamingConvention
{
    public static NamingStyle Classify(string? name)
    {
        if (string.IsNullOrEmpty(name)) return NamingStyle.None;
        var core = name.Trim('_');
        if (core.Length == 0) return NamingStyle.None;
        foreach (var c in core)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return NamingStyle.None;
        }

        var hasUnderscore = core.Contains('_', StringComparison.Ordinal);
        var hasHyphen = core.Contains('-', StringComparison.Ordinal);
        var letters = core.Where(char.IsLetter).ToList();
        if (letters.Count == 0) return NamingStyle.None;
        var allLower = letters.All(char.IsLower);
        var allUpper = letters.All(char.IsUpper);

        if (hasUnderscore && hasHyphen) return NamingStyle.None;
        if (hasUnderscore)
        {
            if (allLower) return NamingStyle.SnakeCase;
            if (allUpper) return NamingStyle.ScreamingSnakeCase;
            return NamingStyle.None;
        }
        if (hasHyphen) return allLower ? NamingStyle.KebabCase : NamingStyle.None;

        // No separator: a single lower-case word fits camel, snake and kebab;
        // an all-caps one fits Pascal and SCREAMING. Neither says anything.
        if (allLower || allUpper) return NamingStyle.None;
        var first = letters[0];
        return char.IsUpper(first) ? NamingStyle.PascalCase : NamingStyle.CamelCase;
    }

    /// <summary>How a convention is written in a finding's message.</summary>
    public static string Label(NamingStyle style) => style switch
    {
        NamingStyle.PascalCase => "PascalCase",
        NamingStyle.CamelCase => "camelCase",
        NamingStyle.SnakeCase => "snake_case",
        NamingStyle.KebabCase => "kebab-case",
        NamingStyle.ScreamingSnakeCase => "SCREAMING_SNAKE_CASE",
        _ => "no convention",
    };

    /// <summary>
    /// The outcome of reading a set of names: which convention most of them
    /// follow, how many of each there are, and whether that is a majority.
    /// </summary>
    public sealed record Tally(
        IReadOnlyDictionary<NamingStyle, int> Counts, NamingStyle Majority, bool Mixed, bool Tied)
    {
        /// <summary>"3 PascalCase, 1 camelCase", largest first.</summary>
        public string Describe() => string.Join(", ", Counts
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Select(kv => $"{kv.Value} {Label(kv.Key)}"));
    }

    /// <summary>Count the voting names. Mixed = two or more conventions present.</summary>
    public static Tally Count(IEnumerable<string> names)
    {
        var counts = new Dictionary<NamingStyle, int>();
        foreach (var name in names)
        {
            var style = Classify(name);
            if (style == NamingStyle.None) continue;
            counts[style] = counts.TryGetValue(style, out var n) ? n + 1 : 1;
        }
        if (counts.Count == 0) return new Tally(counts, NamingStyle.None, false, false);
        var top = counts.Values.Max();
        var leaders = counts.Where(kv => kv.Value == top).Select(kv => kv.Key).ToList();
        var tied = leaders.Count > 1;
        return new Tally(counts, tied ? NamingStyle.None : leaders[0], counts.Count > 1, tied);
    }
}
