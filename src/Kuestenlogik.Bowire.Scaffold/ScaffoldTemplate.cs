// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Text;

namespace Kuestenlogik.Bowire.Scaffold;

/// <summary>
/// The template language of the scaffold templates (#177): a small subset of
/// Mustache, enough for a schema and a stub and small enough to read in one
/// sitting.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><c>{{Name}}</c> — a value; unknown names are an error, not an
///   empty string, so a typo in a template fails the tests instead of
///   shipping a hole.</item>
///   <item><c>{{#Name}}…{{/Name}}</c> — a list repeats the block once per
///   item, whose values shadow the outer ones; <c>true</c> or a non-empty
///   string renders it once; anything else skips it.</item>
///   <item><c>{{^Name}}…{{/Name}}</c> — the inverse: renders when the
///   section would not.</item>
/// </list>
/// Nothing is escaped: the output is source code, and every value comes out
/// of a <see cref="ScaffoldSpec"/> that <see cref="ScaffoldSpec.Normalize"/>
/// already restricted to identifiers.
/// </remarks>
internal static class ScaffoldTemplate
{
    public static string Render(string template, IReadOnlyDictionary<string, object?> model)
    {
        var sb = new StringBuilder(template.Length * 2);
        RenderInto(sb, template, [model]);
        return sb.ToString();
    }

    private static void RenderInto(StringBuilder sb, string template, List<IReadOnlyDictionary<string, object?>> scopes)
    {
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(template, i, template.Length - i);
                return;
            }
            sb.Append(template, i, open - i);
            var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) throw new FormatException($"unclosed tag at {open}");
            var tag = template[(open + 2)..close].Trim();
            i = close + 2;

            if (tag.Length > 1 && (tag[0] == '#' || tag[0] == '^'))
            {
                var name = tag[1..].Trim();
                var endTag = "{{/" + name + "}}";
                var end = FindSectionEnd(template, i, name);
                if (end < 0) throw new FormatException($"section '{name}' is not closed");
                var body = template[i..end];
                i = end + endTag.Length;

                var value = Lookup(scopes, name);
                if (tag[0] == '^')
                {
                    if (!IsTruthy(value)) RenderInto(sb, body, scopes);
                }
                else if (value is IEnumerable list and not string)
                {
                    foreach (var item in list)
                    {
                        if (item is not IReadOnlyDictionary<string, object?> itemScope)
                            throw new FormatException($"section '{name}' holds something other than records");
                        scopes.Insert(0, itemScope);
                        RenderInto(sb, body, scopes);
                        scopes.RemoveAt(0);
                    }
                }
                else if (IsTruthy(value))
                {
                    RenderInto(sb, body, scopes);
                }
                continue;
            }
            if (tag.StartsWith('/')) throw new FormatException($"'{{{{{tag}}}}}' closes a section that was not opened");

            sb.Append(Lookup(scopes, tag) switch
            {
                null => string.Empty,
                bool b => b ? "true" : "false",
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                var v => v.ToString(),
            });
        }
    }

    // Sections of the same name may nest (a field list inside a field list
    // is not something the templates do, but a flag inside a loop is), so
    // count opening and closing tags of that name.
    private static int FindSectionEnd(string template, int from, string name)
    {
        var depth = 1;
        var i = from;
        while (true)
        {
            var next = template.IndexOf("{{", i, StringComparison.Ordinal);
            if (next < 0) return -1;
            var close = template.IndexOf("}}", next + 2, StringComparison.Ordinal);
            if (close < 0) return -1;
            var tag = template[(next + 2)..close].Trim();
            if ((tag.StartsWith('#') || tag.StartsWith('^')) && tag[1..].Trim() == name) depth++;
            else if (tag.StartsWith('/') && tag[1..].Trim() == name && --depth == 0) return next;
            i = close + 2;
        }
    }

    private static object? Lookup(List<IReadOnlyDictionary<string, object?>> scopes, string name)
    {
        foreach (var scope in scopes)
        {
            if (scope.TryGetValue(name, out var value)) return value;
        }
        throw new KeyNotFoundException($"template value '{name}' is not defined");
    }

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        ICollection c => c.Count > 0,
        _ => true,
    };
}
