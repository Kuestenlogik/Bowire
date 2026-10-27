// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>One <c>name: value</c> line of a .bru dictionary block; <c>~</c> marks it disabled.</summary>
internal sealed record BruPair(string Name, string Value, bool Disabled);

/// <summary>One top-level block of a .bru file.</summary>
internal sealed class BruBlock
{
    public string Name { get; init; } = "";

    /// <summary>The <c>name: value</c> lines of a dictionary block.</summary>
    public List<BruPair> Pairs { get; } = [];

    /// <summary>The content of a text block (bodies, scripts, tests, docs), dedented.</summary>
    public string? Text { get; set; }

    /// <summary>The entries of a list block (<c>vars:secret [ … ]</c>).</summary>
    public List<BruPair> Items { get; } = [];

    public string? Get(string name) =>
        Pairs.LastOrDefault(p => !p.Disabled && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}

/// <summary>
/// Bruno's Bru markup: top-level <c>name {</c> … <c>}</c> blocks, either
/// dictionaries of <c>key: value</c> lines or free text (bodies, scripts,
/// tests, docs), and <c>name [</c> … <c>]</c> lists. Content is indented by
/// two spaces; a closing brace at column 0 ends a block.
/// </summary>
internal static partial class BruFile
{
    public static List<BruBlock> Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var blocks = new List<BruBlock>();
        var i = 0;
        while (i < lines.Length)
        {
            var header = BlockHeader().Match(lines[i].TrimEnd());
            i++;
            if (!header.Success) continue;

            var block = new BruBlock { Name = header.Groups["name"].Value };
            var close = header.Groups["open"].Value == "[" ? "]" : "}";
            var body = new List<string>();
            while (i < lines.Length && lines[i].TrimEnd() != close)
            {
                body.Add(lines[i]);
                i++;
            }
            i++; // the closing line

            if (close == "]") ReadList(block, body);
            else if (IsTextBlock(block.Name)) block.Text = Dedent(body, 2);
            else ReadPairs(block, body);
            blocks.Add(block);
        }
        return blocks;
    }

    private static bool IsTextBlock(string name) =>
        name is "tests" or "docs" or "body" or "body:graphql:vars"
        || name.StartsWith("script:", StringComparison.Ordinal)
        || (name.StartsWith("body:", StringComparison.Ordinal)
            && name is not ("body:form-urlencoded" or "body:multipart-form" or "body:file"));

    private static void ReadList(BruBlock block, List<string> body)
    {
        foreach (var raw in string.Join(",", body).Split(','))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;
            var disabled = entry.StartsWith('~');
            block.Items.Add(new BruPair(disabled ? entry[1..] : entry, "", disabled));
        }
    }

    private static void ReadPairs(BruBlock block, List<string> body)
    {
        for (var i = 0; i < body.Count; i++)
        {
            var line = body[i].Trim();
            if (line.Length == 0) continue;

            // @description('''…''') and other annotations, possibly over several lines.
            if (line.StartsWith('@'))
            {
                if (CountTriple(line) == 1)
                {
                    while (i + 1 < body.Count && !body[++i].Contains("'''", StringComparison.Ordinal)) { }
                }
                continue;
            }

            var disabled = line.StartsWith('~');
            if (disabled) line = line[1..];
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (value == "'''")
            {
                var multi = new List<string>();
                while (i + 1 < body.Count && body[i + 1].Trim() != "'''") multi.Add(body[++i]);
                i++; // the closing '''
                value = Dedent(multi, 4);
            }
            block.Pairs.Add(new BruPair(name, value, disabled));
        }
    }

    private static int CountTriple(string line)
    {
        var count = 0;
        for (var at = line.IndexOf("'''", StringComparison.Ordinal); at >= 0; at = line.IndexOf("'''", at + 3, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string Dedent(List<string> lines, int width)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var strip = 0;
            while (strip < width && strip < line.Length && line[strip] == ' ') strip++;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line, strip, line.Length - strip);
        }
        return sb.ToString().TrimEnd();
    }

    [GeneratedRegex(@"^(?<name>[A-Za-z][\w:\-]*)\s*(?<open>[{\[])$")]
    private static partial Regex BlockHeader();
}
