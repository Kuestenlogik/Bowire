// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// The parts of a catalogue string a translation has to keep (#117, #688):
/// its <c>{name}</c> placeholders, and its <c>{name, plural, …}</c> and
/// <c>{name, select, …}</c> blocks with their branches. The same rules the
/// workbench's locale-parity test holds, for <c>bowire docs translations --check</c>.
/// </summary>
internal static partial class MessageShape
{
    /// <summary>A plural or select block: its variable, its type and its branch selectors.</summary>
    internal sealed record Block(string Name, string Type, IReadOnlyList<string> Selectors);

    /// <summary>
    /// The plain <c>{name}</c> placeholders, sorted; <c>{{token}}</c> is not
    /// one, and neither is a one-word branch of a plural or select
    /// (<c>=0 {nothing}</c>): inside a block only the branches' text counts.
    /// </summary>
    public static IReadOnlyList<string> Placeholders(string text) =>
        Placeholder().Matches(WithoutBlockSyntax(text)).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToList();

    // The text with every plural/select block replaced by its branches' text.
    private static string WithoutBlockSyntax(string text)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var head = text[i] == '{' && !(i + 1 < text.Length && text[i + 1] == '{') && !(i > 0 && text[i - 1] == '{')
                ? BlockHead().Match(text, i) : null;
            if (head is not { Success: true } || head.Index != i)
            {
                sb.Append(text[i]);
                continue;
            }
            var depth = 0;
            var end = i;
            for (; end < text.Length; end++)
            {
                if (text[end] == '{') depth++;
                else if (text[end] == '}' && --depth == 0) break;
            }
            var rest = text[(i + head.Length)..Math.Min(end, text.Length)];
            var d = 0;
            var body = new System.Text.StringBuilder();
            foreach (var ch in rest)
            {
                if (ch == '{' && d++ == 0) continue;
                if (ch == '}' && --d == 0) { sb.Append(WithoutBlockSyntax(body.ToString())).Append(' '); body.Clear(); continue; }
                if (d > 0) body.Append(ch);
            }
            i = end;
        }
        return sb.ToString();
    }

    /// <summary>Every plural and select block in <paramref name="text"/>, nested ones included.</summary>
    public static IReadOnlyList<Block> Blocks(string text)
    {
        var blocks = new List<Block>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{' || (i + 1 < text.Length && text[i + 1] == '{') || (i > 0 && text[i - 1] == '{')) continue;
            var head = BlockHead().Match(text, i);
            if (!head.Success || head.Index != i) continue;

            var depth = 0;
            var end = i;
            for (; end < text.Length; end++)
            {
                if (text[end] == '{') depth++;
                else if (text[end] == '}' && --depth == 0) break;
            }
            var rest = text[(i + head.Length)..Math.Min(end, text.Length)];
            var selectors = new List<string>();
            var d = 0;
            var token = new System.Text.StringBuilder();
            foreach (var ch in rest)
            {
                if (ch == '{')
                {
                    if (d == 0 && token.ToString().Trim() is { Length: > 0 } selector) selectors.Add(selector);
                    d++;
                    token.Clear();
                }
                else if (ch == '}') d--;
                else if (d == 0) token.Append(ch);
            }
            blocks.Add(new Block(head.Groups[1].Value, head.Groups[2].Value, selectors));
        }
        return blocks;
    }

    /// <summary>
    /// What is wrong with <paramref name="translated"/> against
    /// <paramref name="english"/> for <paramref name="locale"/>, or an empty
    /// list. A plural must carry the English exact branches (<c>=0</c>) and
    /// exactly the language's own categories; a select the same values as
    /// English.
    /// </summary>
    public static IReadOnlyList<string> Problems(string english, string translated, string locale)
    {
        var problems = new List<string>();
        var want = Placeholders(english);
        var have = Placeholders(translated);
        if (!want.SequenceEqual(have, StringComparer.Ordinal))
            problems.Add($"placeholders {{{string.Join("} {", want)}}} expected, {{{string.Join("} {", have)}}} found");

        var englishBlocks = Blocks(english);
        var translatedBlocks = Blocks(translated);
        static string Shape(IReadOnlyList<Block> bs) =>
            string.Join(",", bs.Select(b => b.Name + ":" + b.Type).Order(StringComparer.Ordinal));
        if (Shape(englishBlocks) != Shape(translatedBlocks))
        {
            problems.Add($"blocks {Shape(englishBlocks)} expected, {Shape(translatedBlocks)} found");
            return problems;
        }

        var categories = CategoriesOf(locale);
        foreach (var block in translatedBlocks)
        {
            var en = englishBlocks.First(b => b.Name == block.Name && b.Type == block.Type);
            var got = block.Selectors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (block.Type == "select")
            {
                var expected = en.Selectors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                if (!expected.SequenceEqual(got, StringComparer.Ordinal))
                    problems.Add($"{{{block.Name}, select}} needs {string.Join(" ", expected)}, has {string.Join(" ", got)}");
                continue;
            }

            var exact = en.Selectors.Where(s => s.StartsWith('=')).ToList();
            if (categories is null)
            {
                // A language whose rules are not in the table: at least the
                // fallback, and nothing that is no CLDR category.
                var bad = got.Where(s => !s.StartsWith('=') && !s_allCategories.Contains(s)).ToList();
                if (!got.Contains("other") || bad.Count > 0)
                    problems.Add($"{{{block.Name}, plural}} needs an other branch and CLDR categories only, has {string.Join(" ", got)}");
                continue;
            }
            var need = exact.Concat(categories).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (!need.SequenceEqual(got, StringComparer.Ordinal))
                problems.Add($"{{{block.Name}, plural}} needs {string.Join(" ", need)} in {locale}, has {string.Join(" ", got)}");
        }
        return problems;
    }

    private static readonly HashSet<string> s_allCategories = new(StringComparer.Ordinal) { "zero", "one", "two", "few", "many", "other" };

    // CLDR cardinal categories per language, embedded as data (.NET has no
    // Intl.PluralRules). A regional locale (de-AT) falls back to its language.
    private static readonly Lazy<Dictionary<string, string[]>> s_categories = new(() =>
    {
        using var stream = typeof(MessageShape).Assembly.GetManifestResourceStream("Kuestenlogik.Bowire.App.Cli.plural-categories.json")
            ?? throw new InvalidOperationException("plural-categories.json is not embedded");
        using var doc = JsonDocument.Parse(stream);
        var table = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Array)
                table[p.Name] = p.Value.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        }
        return table;
    });

    /// <summary>The plural categories of <paramref name="locale"/>, or <c>null</c> when the table has none.</summary>
    public static IReadOnlyList<string>? CategoriesOf(string locale)
    {
        var table = s_categories.Value;
        if (table.TryGetValue(locale, out var exact)) return exact;
        var language = locale.Split('-', '_')[0];
        return table.TryGetValue(language, out var byLanguage) ? byLanguage : null;
    }

    [GeneratedRegex(@"(?<!\{)\{([a-zA-Z0-9_]+)\}(?!\})")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\G\{\s*([A-Za-z0-9_]+)\s*,\s*(plural|select)\s*,")]
    private static partial Regex BlockHead();
}
