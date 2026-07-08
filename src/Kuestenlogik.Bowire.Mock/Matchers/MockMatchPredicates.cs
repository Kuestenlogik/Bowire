// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Mock.Matchers;

/// <summary>
/// Evaluation of the optional <see cref="BowireStepMatch"/> predicates a
/// recording step can layer on top of the verb + path match (#402): query,
/// header, and cookie predicates, plus the regex / glob path patterns. Kept
/// separate from <see cref="ExactMatcher"/> so the parsing + comparison logic
/// is independently testable.
/// </summary>
internal static class MockMatchPredicates
{
    // Compiled regexes are cached by pattern — the matcher is shared across
    // every request, and a stub's regex/glob is fixed for the life of the
    // recording, so recompiling per request would be pure waste.
    private static readonly ConcurrentDictionary<string, Regex> s_pathRegexCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Regex> s_predicateRegexCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Regex> s_globCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether every query / header / cookie predicate declared on
    /// <paramref name="match"/> passes for <paramref name="request"/>. A null
    /// match (or one with no predicates) trivially passes — the path pattern
    /// arms are handled by the caller, not here.
    /// </summary>
    public static bool AllPredicatesPass(BowireStepMatch match, MockRequest request)
    {
        if (match.Query is { Count: > 0 } q)
        {
            var query = ParseQuery(request.Query);
            foreach (var p in q)
                if (!EvaluatePredicate(p, Lookup(query, p.Name))) return false;
        }

        if (match.Headers is { Count: > 0 } h)
        {
            foreach (var p in h)
            {
                // HTTP header names are case-insensitive; MockRequest.Headers is
                // already an OrdinalIgnoreCase dictionary.
                request.Headers.TryGetValue(p.Name, out var raw);
                var values = raw is null ? null : SplitHeaderValues(raw);
                if (!EvaluatePredicate(p, values)) return false;
            }
        }

        if (match.Cookies is { Count: > 0 } c)
        {
            var cookies = ParseCookies(request.Headers);
            foreach (var p in c)
                if (!EvaluatePredicate(p, Lookup(cookies, p.Name))) return false;
        }

        return true;
    }

    /// <summary>Does the request path satisfy the match's regex path pattern?</summary>
    public static bool PathRegexMatches(string pattern, string path)
        => s_pathRegexCache.GetOrAdd(pattern, CompileAnchoredRegex).IsMatch(path);

    /// <summary>Does the request path satisfy the match's glob path pattern?</summary>
    public static bool PathGlobMatches(string glob, string path)
        => s_globCache.GetOrAdd(glob, GlobToRegex).IsMatch(path);

    // ---- predicate evaluation ----

    /// <summary>
    /// Evaluate one predicate against the value(s) found for its name.
    /// <paramref name="values"/> is null / empty when the name is absent.
    /// </summary>
    internal static bool EvaluatePredicate(BowireMatchPredicate predicate, IReadOnlyList<string>? values)
    {
        var present = values is { Count: > 0 };

        // Negative predicate: the name must NOT be present.
        if (predicate.Present == false) return !present;

        // Any positive predicate requires the name to be present at all.
        if (!present) return false;

        var hasOperator = predicate.EqualTo is not null
            || predicate.Matches is not null
            || predicate.Contains is not null;
        if (!hasOperator) return true; // presence-only predicate, satisfied

        var comparison = predicate.CaseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var value in values!)
        {
            if (predicate.EqualTo is not null && string.Equals(value, predicate.EqualTo, comparison))
                return true;
            if (predicate.Contains is not null && value.Contains(predicate.Contains, comparison))
                return true;
            if (predicate.Matches is not null && RegexMatches(predicate.Matches, value, predicate.CaseInsensitive))
                return true;
        }
        return false;
    }

    private static bool RegexMatches(string pattern, string value, bool caseInsensitive)
    {
        var key = caseInsensitive ? "i:" + pattern : pattern;
        var regex = s_predicateRegexCache.GetOrAdd(key, _ => new Regex(
            pattern,
            RegexOptions.CultureInvariant | RegexOptions.Compiled
                | (caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None)));
        return regex.IsMatch(value);
    }

    // ---- parsing helpers ----

    private static List<string>? Lookup(Dictionary<string, List<string>> map, string name)
        => map.TryGetValue(name, out var values) ? values : null;

    /// <summary>
    /// Parse a raw query string (with or without the leading <c>?</c>) into a
    /// name → values map. Names are compared case-sensitively (per the URI
    /// spec); repeated params accumulate. Values are percent-decoded.
    /// </summary>
    internal static Dictionary<string, List<string>> ParseQuery(string? query)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query)) return map;

        var span = query.AsSpan();
        if (span.Length > 0 && span[0] == '?') span = span[1..];

        foreach (var pairRange in span.ToString().Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pairRange.IndexOf('=');
            string name, value;
            if (eq < 0)
            {
                name = Decode(pairRange);
                value = string.Empty;
            }
            else
            {
                name = Decode(pairRange.AsSpan(0, eq));
                value = Decode(pairRange.AsSpan(eq + 1));
            }
            if (name.Length == 0) continue;
            Add(map, name, value);
        }
        return map;
    }

    /// <summary>
    /// Parse the <c>Cookie</c> request header into a name → values map. The
    /// header is a <c>; </c>-separated list of <c>name=value</c> pairs. Cookie
    /// names are compared case-sensitively.
    /// </summary>
    internal static Dictionary<string, List<string>> ParseCookies(IDictionary<string, string> headers)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (!headers.TryGetValue("Cookie", out var cookieHeader) || string.IsNullOrEmpty(cookieHeader))
            return map;

        foreach (var part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (name.Length == 0) continue;
            Add(map, name, value);
        }
        return map;
    }

    // A header value copied from ASP.NET's StringValues is comma-joined when
    // the header appeared multiple times. Split so a predicate can match any
    // single value; keep the raw value too in case a comma is literal.
    private static List<string> SplitHeaderValues(string raw)
    {
        var list = new List<string> { raw };
        if (raw.Contains(',', StringComparison.Ordinal))
        {
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                list.Add(part);
        }
        return list;
    }

    private static void Add(Dictionary<string, List<string>> map, string name, string value)
    {
        if (!map.TryGetValue(name, out var list))
        {
            list = [];
            map[name] = list;
        }
        list.Add(value);
    }

    private static string Decode(ReadOnlySpan<char> raw)
    {
        var s = raw.ToString();
        // '+' means space in application/x-www-form-urlencoded query strings.
        if (s.Contains('+', StringComparison.Ordinal)) s = s.Replace('+', ' ');
        try { return Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return s; }
    }

    // ---- pattern compilation ----

    private static Regex CompileAnchoredRegex(string pattern)
    {
        var anchored = pattern;
        if (!anchored.StartsWith('^')) anchored = "^" + anchored;
        if (!anchored.EndsWith('$')) anchored += "$";
        return new Regex(anchored, RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    /// <summary>
    /// Translate a path glob to an anchored regex. <c>**</c> → <c>.*</c>
    /// (crosses <c>/</c>), <c>*</c> → <c>[^/]*</c> (one segment), <c>?</c> →
    /// <c>[^/]</c>. Every other character is regex-escaped so path punctuation
    /// stays literal.
    /// </summary>
    internal static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var ch = glob[i];
            if (ch == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++; // consume the second '*'
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (ch == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(ch.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
