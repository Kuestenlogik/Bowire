// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>
/// Insomnia's Nunjucks templates in Bowire's variable syntax:
/// <c>{{ _.base_url }}</c> and <c>{{ base_url }}</c> become <c>{{base_url}}</c>,
/// the <c>{% uuid %}</c> / <c>{% now %}</c> tags become system variables. Every
/// other tag (response chaining, faker, file, prompt, …) stays as written and
/// is named in <c>unmapped</c>.
/// </summary>
internal static partial class InsomniaTemplates
{
    public static string ToBowire(string text, ICollection<string> unmapped)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains("{{", StringComparison.Ordinal) && !text.Contains("{%", StringComparison.Ordinal)) return text;

        var withVars = Variable().Replace(text, m =>
        {
            var name = m.Groups["name"].Value;
            // _['my key'] → my key; Bowire names are flat.
            var bracket = BracketName().Match(name);
            if (bracket.Success) name = bracket.Groups[1].Value;
            if (name.StartsWith("_.", StringComparison.Ordinal)) name = name[2..];
            if (name.Contains('|', StringComparison.Ordinal))
            {
                unmapped.Add("filter in {{ " + name.Trim() + " }}");
                return m.Value;
            }
            return "{{" + name.Trim() + "}}";
        });

        return Tag().Replace(withVars, m =>
        {
            var tag = m.Groups["tag"].Value;
            var args = m.Groups["args"].Value;
            switch (tag)
            {
                case "uuid":
                    return "{{uuid}}";
                case "now":
                    if (args.Contains("millis", StringComparison.Ordinal)) return "{{nowMs}}";
                    if (args.Contains("unix", StringComparison.Ordinal)) return "{{now}}";
                    if (args.Length == 0 || args.Contains("iso", StringComparison.Ordinal)) return "{{timestamp}}";
                    unmapped.Add("{% now " + args.Trim() + " %}");
                    return m.Value;
                case "response":
                    unmapped.Add("{% response %} (rewrite as {{response.path}} chaining in Bowire)");
                    return m.Value;
                default:
                    unmapped.Add("{% " + tag + " %}");
                    return m.Value;
            }
        });
    }

    [GeneratedRegex(@"\{\{\s*(?<name>[^{}%]+?)\s*\}\}")]
    private static partial Regex Variable();

    [GeneratedRegex(@"^_\[\s*['""](.+)['""]\s*\]$")]
    private static partial Regex BracketName();

    [GeneratedRegex(@"\{%\s*(?<tag>\w+)(?<args>[^%]*?)\s*%\}")]
    private static partial Regex Tag();
}
