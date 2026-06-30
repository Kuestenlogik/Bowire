// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Minimal C# port of the workbench's <c>substituteVars</c> JS helper
/// (<c>src/Kuestenlogik.Bowire/wwwroot/js/history-env.js</c>) — covers the
/// subset T2 needs: <c>{{name}}</c> and <c>${name}</c> resolved against a
/// flat env map, plus the canonical <c>now</c> / <c>nowMs</c> /
/// <c>timestamp</c> / <c>uuid</c> / <c>random</c> system variables. Step
/// chaining (<c>{{step1.response.id}}</c>) and the
/// <c>env.</c> / <c>runtime.</c> / <c>secret.</c> prefixes are out of scope
/// for v0 — every shipped example flow uses the bare-name syntax and a
/// runtime-side step chaining pass would itself need step-result storage
/// the v0 runner doesn't yet thread through.
/// </summary>
internal static class FlowVariableResolver
{
    // Bash-style ${name} placeholders. ${$name} (escaped) keeps the literal.
    private static readonly Regex DollarPlaceholder = new(
        @"\$(\$?)\{([^}]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    // Postman / v2.2 canonical {{name}} placeholders. {{{{name}}}} (escape) keeps literal.
    private static readonly Regex CurlyPlaceholder = new(
        @"(\{\{\{\{[^}]+\}\}\}\})|(\{\{([^{}]+)\}\})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Replace every <c>{{name}}</c> and <c>${name}</c> in
    /// <paramref name="input"/> with its merged-env value (then its
    /// system-variable fallback). Unknown names are left intact so the
    /// operator notices the typo in the failing assertion's <c>actual</c>
    /// readout.
    /// </summary>
    public static string Resolve(string input, IReadOnlyDictionary<string, string> env)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var step1 = input.Contains("${", StringComparison.Ordinal)
            ? DollarPlaceholder.Replace(input, m => ReplaceDollar(m, env))
            : input;
        var step2 = step1.Contains("{{", StringComparison.Ordinal)
            ? CurlyPlaceholder.Replace(step1, m => ReplaceCurly(m, env))
            : step1;
        return step2;
    }

    private static string ReplaceDollar(Match m, IReadOnlyDictionary<string, string> env)
    {
        // ${$NAME} is the escape — emit a literal ${NAME}.
        if (m.Groups[1].Length > 0) return "${" + m.Groups[2].Value + "}";
        var key = m.Groups[2].Value.Trim();
        return ResolveKey(key, env) ?? m.Value;
    }

    private static string ReplaceCurly(Match m, IReadOnlyDictionary<string, string> env)
    {
        // {{{{NAME}}}} is the Mustache-style escape — strip one layer so
        // the literal {{NAME}} stays visible.
        if (m.Groups[1].Length > 0)
        {
            var inner = m.Groups[1].Value;
            return inner.Substring(2, inner.Length - 4);
        }
        var key = m.Groups[3].Value.Trim();
        return ResolveKey(key, env) ?? m.Value;
    }

    private static string? ResolveKey(string key, IReadOnlyDictionary<string, string> env)
    {
        // System variables take precedence — matches the JS helper's
        // shadowing rule so a flow that relies on {{uuid}} can't be
        // accidentally broken by a stray env entry of the same name.
        var sys = ResolveSystemVar(key);
        if (sys is not null) return sys;
        return env.TryGetValue(key, out var v) ? v : null;
    }

    private static string? ResolveSystemVar(string key)
    {
        switch (key)
        {
            case "now":
                return DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            case "nowMs":
                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            case "timestamp":
                return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            case "uuid":
                return Guid.NewGuid().ToString();
            case "random":
                return System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue)
                    .ToString(CultureInfo.InvariantCulture);
        }
        var nowOffset = Regex.Match(key, @"^now([+-])(\d+)$", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        if (nowOffset.Success)
        {
            var baseSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var offset = long.Parse(nowOffset.Groups[2].Value, CultureInfo.InvariantCulture);
            return (nowOffset.Groups[1].Value == "+" ? baseSec + offset : baseSec - offset)
                .ToString(CultureInfo.InvariantCulture);
        }
        return null;
    }
}
