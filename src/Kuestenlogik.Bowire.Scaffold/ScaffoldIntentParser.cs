// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Scaffold;

/// <summary>
/// Reads a spec out of a sentence without a model (#177) — what scaffolding
/// falls back to when no AI provider answers, and what the CLI uses unless
/// told otherwise.
/// </summary>
/// <remarks>
/// It understands the phrasing the ticket uses and little more:
/// <c>REST CRUD for User with email + role</c>,
/// <c>gRPC service for orders with customer (required), total: double, paid bool</c>.
/// The protocol comes from <c>rest</c> / <c>grpc</c> in the sentence, the
/// entity from the word after <c>for</c> (or <c>für</c>), the fields from
/// the list after <c>with</c> (or <c>mit</c>), split on commas, <c>+</c>,
/// <c>and</c> / <c>und</c>. A field takes a type from a type word next to it
/// and is required when it says <c>required</c>, <c>pflicht</c> or ends in
/// <c>*</c>. Whatever it had to assume, it says in <c>Notes</c>.
/// </remarks>
public static class ScaffoldIntentParser
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMilliseconds(100);

    private static readonly HashSet<string> s_notEntity = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "ein", "eine", "einen", "crud", "rest", "grpc", "api", "service", "simple", "basic", "new",
    };

    private static readonly HashSet<string> s_requiredWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "required", "mandatory", "pflicht", "pflichtfeld", "not-null", "notnull",
    };

    private static readonly HashSet<string> s_fillerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "field", "fields", "as", "of", "type", "ein", "eine", "feld", "felder", "optional",
    };

    /// <summary>Parse <paramref name="intent"/>; <paramref name="protocol"/> wins over the sentence when given.</summary>
    public static (ScaffoldSpec Spec, IReadOnlyList<string> Notes) Parse(string intent, string? protocol = null)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var notes = new List<string>();
        var text = intent.Trim();

        var proto = (protocol is null ? null : ScaffoldNames.Lower(protocol.Trim()));
        if (string.IsNullOrEmpty(proto))
        {
            proto = Regex.IsMatch(text, @"\b(grpc|proto(buf)?)\b", RegexOptions.IgnoreCase, s_timeout) ? "grpc" : "rest";
            if (!Regex.IsMatch(text, @"\b(grpc|proto(buf)?|rest|http|openapi)\b", RegexOptions.IgnoreCase, s_timeout))
                notes.Add("no protocol named; assumed REST");
        }

        var entity = FindEntity(text);
        if (entity is null)
        {
            entity = "Item";
            notes.Add("no entity found (\"… for <Entity> with …\"); assumed Item");
        }

        var fields = FindFields(text);
        if (fields.Count == 0)
        {
            fields.Add(new ScaffoldField("name", "string", Required: true));
            notes.Add("no fields found (\"… with a, b, c\"); assumed a required name");
        }

        return (new ScaffoldSpec(ScaffoldSpec.Pascal(ScaffoldNames.Singular(entity)), proto, fields), notes);
    }

    private static string? FindEntity(string text)
    {
        foreach (Match m in Regex.Matches(text, @"\b(?:for|für|of|called|named|manage|managing|verwaltet)\s+(?:(?:a|an|the|ein|eine|einen|all|alle)\s+)?([A-Za-zÄÖÜäöüß][\w-]*)", RegexOptions.IgnoreCase, s_timeout))
        {
            var word = m.Groups[1].Value;
            if (!s_notEntity.Contains(word)) return Ascii(word);
        }
        // "User CRUD", "Orders API": a capitalised word before the list.
        var head = SplitAtFieldList(text).Head;
        foreach (Match m in Regex.Matches(head, @"\b([A-Z][\w-]*)", RegexOptions.None, s_timeout))
        {
            if (!s_notEntity.Contains(m.Groups[1].Value)) return Ascii(m.Groups[1].Value);
        }
        return null;
    }

    private static List<ScaffoldField> FindFields(string text)
    {
        var list = SplitAtFieldList(text).List;
        var fields = new List<ScaffoldField>();
        if (list.Length == 0) return fields;

        // Commas inside "(string, required)" must not split the field, so
        // protect parenthesised groups first.
        var protectedList = Regex.Replace(list, @"\(([^)]*)\)", m => "(" + m.Groups[1].Value.Replace(',', ' ') + ")", RegexOptions.None, s_timeout);
        var chunks = Regex.Split(protectedList, @"\s*(?:,|;|\+|&|\band\b|\bund\b)\s*", RegexOptions.IgnoreCase, s_timeout);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in chunks)
        {
            var chunk = raw.Trim().TrimEnd('.');
            if (chunk.Length == 0) continue;
            var required = chunk.EndsWith('*') || chunk.EndsWith('!');
            var words = Regex.Split(chunk.Trim('*', '!'), @"[\s:()]+", RegexOptions.None, s_timeout)
                .Where(w => w.Length > 0).ToList();
            string? type = null;
            var nameWords = new List<string>();
            foreach (var w in words)
            {
                if (s_requiredWords.Contains(w)) { required = true; continue; }
                if (s_fillerWords.Contains(w)) continue;
                var t = ScaffoldNames.NormalizeType(w);
                // A type word only counts as a type once a name is there,
                // or when it is also a good name ("email", "url"): then it
                // is both.
                if (t is not null && !string.Equals(w, "string", StringComparison.OrdinalIgnoreCase) && nameWords.Count == 0
                    && ScaffoldNames.Lower(w) is "email" or "url" or "date" or "timestamp")
                {
                    nameWords.Add(w);
                    type ??= t;
                    continue;
                }
                if (t is not null && nameWords.Count > 0) { type = t; continue; }
                nameWords.Add(w);
            }
            if (nameWords.Count == 0) continue;
            var name = ScaffoldSpec.Camel(Ascii(string.Join(' ', nameWords).Replace("e-mail", "email", StringComparison.OrdinalIgnoreCase)));
            if (name.Length == 0 || string.Equals(name, "id", StringComparison.OrdinalIgnoreCase) || !seen.Add(name)) continue;
            fields.Add(new ScaffoldField(name, type ?? GuessType(name), required));
        }
        return fields;
    }

    // Names that say what they hold.
    private static string GuessType(string name)
    {
        var n = ScaffoldNames.Lower(name);
        if (n.EndsWith("at", StringComparison.Ordinal) && n.Length > 3 && char.IsUpper(name[^2])) return "datetime"; // createdAt
        if (n.StartsWith("is", StringComparison.Ordinal) && name.Length > 2 && char.IsUpper(name[2])) return "bool";   // isActive
        if (n.StartsWith("has", StringComparison.Ordinal) && name.Length > 3 && char.IsUpper(name[3])) return "bool";
        if (n is "age" or "count" or "quantity" or "year") return "int";
        if (n is "price" or "amount" or "total" or "balance") return "double";
        if (n.EndsWith("date", StringComparison.Ordinal) || n is "birthday") return "datetime";
        return "string";
    }

    private static (string Head, string List) SplitAtFieldList(string text)
    {
        var m = Regex.Match(text, @"\b(?:with|having|mit|fields?\s*:)\s+", RegexOptions.IgnoreCase, s_timeout);
        return m.Success ? (text[..m.Index], text[(m.Index + m.Length)..]) : (text, string.Empty);
    }

    // Identifiers are ASCII; "Größe" becomes "Groesse" rather than failing.
    private static string Ascii(string word) => word
        .Replace("ä", "ae", StringComparison.Ordinal).Replace("ö", "oe", StringComparison.Ordinal).Replace("ü", "ue", StringComparison.Ordinal)
        .Replace("Ä", "Ae", StringComparison.Ordinal).Replace("Ö", "Oe", StringComparison.Ordinal).Replace("Ü", "Ue", StringComparison.Ordinal)
        .Replace("ß", "ss", StringComparison.Ordinal);
}
