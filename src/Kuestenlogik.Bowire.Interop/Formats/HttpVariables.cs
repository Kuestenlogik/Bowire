// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>
/// Variable references between Bowire and .http files. Both use <c>{{name}}</c>;
/// they differ in the dynamic values (<c>{{$uuid}}</c> against Bowire's
/// <c>{{uuid}}</c>) and in secrets, which Bowire names <c>{{secret.x}}</c> and
/// a .http file keeps in its private environment file.
/// </summary>
internal static partial class HttpVariables
{
    // .http dynamic variable → Bowire system variable.
    private static readonly Dictionary<string, string> s_toBowire = new(StringComparer.Ordinal)
    {
        ["$uuid"] = "uuid",
        ["$random.uuid"] = "uuid",
        ["$guid"] = "uuid",
        ["$timestamp"] = "now",
        ["$isoTimestamp"] = "timestamp",
        ["$randomInt"] = "random",
    };

    /// <summary>
    /// Rewrite the dynamic values of <paramref name="text"/> to Bowire's; the
    /// ones Bowire has no equivalent for are returned in <paramref name="unmapped"/>.
    /// </summary>
    public static string ToBowire(string text, ICollection<string> unmapped)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Reference().Replace(text, m =>
        {
            var name = m.Groups[1].Value.Trim();
            if (!name.StartsWith('$')) return m.Value;
            if (s_toBowire.TryGetValue(name, out var bowire)) return "{{" + bowire + "}}";
            unmapped.Add(name);
            return m.Value;
        });
    }

    /// <summary>
    /// Rewrite Bowire references in <paramref name="text"/> for a .http file:
    /// <c>${x}</c> becomes <c>{{x}}</c>, system values become dynamic
    /// variables, and <c>{{secret.x}}</c> / <c>{{keyring.svc/acct}}</c> become
    /// plain names collected in <paramref name="privateNames"/> for the private
    /// environment file. Secrets are never resolved: only their names leave Bowire.
    /// </summary>
    public static string FromBowire(string text, HttpDialect dialect, ISet<string> privateNames, ICollection<string> unmapped)
    {
        ArgumentNullException.ThrowIfNull(text);
        var curly = Legacy().Replace(text, m => "{{" + m.Groups[1].Value + "}}");
        return Reference().Replace(curly, m =>
        {
            var name = m.Groups[1].Value.Trim();
            switch (name)
            {
                case "uuid": return dialect == HttpDialect.VsCode ? "{{$guid}}" : "{{$uuid}}";
                case "now": return "{{$timestamp}}";
                case "timestamp": return dialect == HttpDialect.VsCode ? "{{$datetime iso8601}}" : "{{$isoTimestamp}}";
                case "random": return dialect == HttpDialect.VsCode ? "{{$randomInt 0 2147483647}}" : "{{$randomInt}}";
            }
            if (name.StartsWith("secret.", StringComparison.Ordinal) || name.StartsWith("keyring.", StringComparison.Ordinal))
            {
                var plain = PlainName(name[(name.IndexOf('.', StringComparison.Ordinal) + 1)..]);
                privateNames.Add(plain);
                return "{{" + plain + "}}";
            }
            if (name.StartsWith("env.", StringComparison.Ordinal)) return "{{" + name[4..] + "}}";
            // Response chaining, steps, runtime and AI values have no .http
            // counterpart that works without a script.
            if (name.StartsWith("response.", StringComparison.Ordinal) || name.StartsWith("prev.", StringComparison.Ordinal)
                || name.StartsWith("runtime.", StringComparison.Ordinal) || name.StartsWith("ai.", StringComparison.Ordinal)
                || StepReference().IsMatch(name))
            {
                unmapped.Add(name);
            }
            return m.Value;
        });
    }

    /// <summary>The names <paramref name="text"/> references, without dynamic values.</summary>
    public static IEnumerable<string> Names(string text) =>
        Reference().Matches(text).Select(m => m.Groups[1].Value.Trim()).Where(n => !n.StartsWith('$')).Distinct(StringComparer.Ordinal);

    // keyring.svc/acct → svc_acct: a name every .http client accepts.
    private static string PlainName(string name) => InvalidNameChars().Replace(name, "_");

    [GeneratedRegex(@"\{\{([^{}]+)\}\}")]
    private static partial Regex Reference();

    [GeneratedRegex(@"\$\{([A-Za-z_][\w.\-]*)\}")]
    private static partial Regex Legacy();

    [GeneratedRegex(@"^step\d+\.")]
    private static partial Regex StepReference();

    [GeneratedRegex(@"[^A-Za-z0-9_\-]")]
    private static partial Regex InvalidNameChars();
}
