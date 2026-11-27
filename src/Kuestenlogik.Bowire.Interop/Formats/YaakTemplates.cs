// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>
/// Yaak's templates in Bowire's variable syntax (#939): <c>${[ base_url ]}</c>
/// becomes <c>{{base_url}}</c>, and the template functions with a Bowire
/// counterpart become system variables — <c>uuid.v4()</c> <c>{{uuid}}</c>,
/// <c>timestamp.iso8601()</c> <c>{{timestamp}}</c>,
/// <c>timestamp.unix()</c> <c>{{now}}</c>, <c>timestamp.unixMillis()</c>
/// <c>{{nowMs}}</c>. Every other function (response chaining, prompts,
/// encryption, files, …) stays as written and is named in <c>unmapped</c>.
/// </summary>
internal static partial class YaakTemplates
{
    public static string ToBowire(string text, ICollection<string> unmapped)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains("${[", StringComparison.Ordinal)) return text;

        return Template().Replace(text, m =>
        {
            var inner = m.Groups["inner"].Value.Trim();
            if (Name().IsMatch(inner)) return "{{" + inner + "}}";

            var call = Call().Match(inner);
            if (!call.Success)
            {
                unmapped.Add("${[ " + inner + " ]}");
                return m.Value;
            }
            var function = call.Groups["fn"].Value;
            switch (function)
            {
                case "uuid.v4":
                case "uuid.v7":
                    return "{{uuid}}";
                case "timestamp.iso8601":
                    return "{{timestamp}}";
                case "timestamp.unix":
                    return "{{now}}";
                case "timestamp.unixMillis":
                    return "{{nowMs}}";
                default:
                    unmapped.Add(function);
                    return m.Value;
            }
        });
    }

    /// <summary>The report line for a template named in <c>unmapped</c>.</summary>
    public static string Unmapped(string template) =>
        template.StartsWith("response.", StringComparison.Ordinal)
            // The functions with a counterpart that cannot be written for the
            // caller: which response and which path is in their arguments.
            ? $"${{[ {template}(…) ]}} stays as written; rewrite it as {{{{response.path}}}} chaining in Bowire"
            : template.StartsWith("${[", StringComparison.Ordinal)
                ? $"{template} stays as written"
                : $"${{[ {template}(…) ]}} has no Bowire equivalent and stays as written";

    [GeneratedRegex(@"\$\{\[(?<inner>.*?)\]\}", RegexOptions.Singleline)]
    private static partial Regex Template();

    [GeneratedRegex(@"^[A-Za-z_][\w.\-]*$")]
    private static partial Regex Name();

    [GeneratedRegex(@"^(?<fn>[A-Za-z_][\w.]*)\s*\(.*\)$", RegexOptions.Singleline)]
    private static partial Regex Call();
}
