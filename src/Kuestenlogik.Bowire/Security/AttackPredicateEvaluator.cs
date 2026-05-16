// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Security;

/// <summary>
/// Stateless walker that evaluates an <see cref="AttackPredicate"/>
/// against an <see cref="AttackProbeResponse"/>. Returns
/// <see langword="true"/> when the predicate matches — i.e. when the
/// target is vulnerable according to the template.
/// </summary>
/// <remarks>
/// <para>
/// All operators on a single node implicit-AND-combine. The composite
/// operators (<c>allOf</c> / <c>anyOf</c> / <c>not</c>) recurse. An
/// empty predicate (no operators set) matches by definition — treated
/// as "no condition specified, so trivially true". Templates that
/// supply no <see cref="AttackPredicate"/> at all should never reach
/// this evaluator; the scanner subcommand guards against that.
/// </para>
/// <para>
/// JSONPath evaluation supports the same subset the JS-side
/// <c>bowireResolveJsonPath</c> in the workbench supports:
/// <c>$</c>, <c>$.foo</c>, <c>$.foo.bar</c>, <c>$.foo[0]</c>,
/// <c>$.foo[*]</c>, <c>$.foo[*].bar</c>. Wildcards return every match;
/// the operators interpret "any value matches" / "all match" / "exists"
/// over the wildcard expansion.
/// </para>
/// </remarks>
public static class AttackPredicateEvaluator
{
    /// <summary>
    /// Evaluate the predicate against the response. Returns
    /// <see langword="true"/> when the response indicates the target is
    /// vulnerable; <see langword="false"/> otherwise.
    /// </summary>
    public static bool Evaluate(AttackPredicate predicate, AttackProbeResponse response)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(response);

        if (predicate.Status is int wantStatus
            && response.Status != wantStatus) return false;

        if (predicate.StatusIn is { Count: > 0 } statusSet
            && !statusSet.Contains(response.Status)) return false;

        if (!string.IsNullOrEmpty(predicate.BodyContains)
            && response.Body.IndexOf(predicate.BodyContains, StringComparison.Ordinal) < 0) return false;

        if (!string.IsNullOrEmpty(predicate.BodyMatches))
        {
            try
            {
                if (!Regex.IsMatch(response.Body, predicate.BodyMatches,
                        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) return false;
            }
            catch (RegexMatchTimeoutException) { return false; }
            catch (ArgumentException) { return false; /* invalid regex in template */ }
        }

        if (predicate.BodyJsonPath is { } jsonPathClause
            && !EvaluateJsonPath(jsonPathClause, response.Body)) return false;

        if (predicate.HeaderEquals is { Count: > 0 } headerEqMap)
        {
            foreach (var (name, want) in headerEqMap)
            {
                if (!response.Headers.TryGetValue(name, out var got)
                    || !string.Equals(got, want, StringComparison.Ordinal)) return false;
            }
        }

        if (predicate.HeaderExists is { Count: > 0 } headerExistsList)
        {
            foreach (var name in headerExistsList)
            {
                if (!response.Headers.ContainsKey(name)) return false;
            }
        }

        if (predicate.HeaderMissing is { Count: > 0 } headerMissingList)
        {
            foreach (var name in headerMissingList)
            {
                if (response.Headers.ContainsKey(name)) return false;
            }
        }

        if (predicate.LatencyMsAtLeast is int wantLatency
            && response.LatencyMs < wantLatency) return false;

        if (predicate.AllOf is { Count: > 0 } allOf)
        {
            foreach (var child in allOf)
            {
                if (!Evaluate(child, response)) return false;
            }
        }

        if (predicate.AnyOf is { Count: > 0 } anyOf)
        {
            var matched = false;
            foreach (var child in anyOf)
            {
                if (Evaluate(child, response)) { matched = true; break; }
            }
            if (!matched) return false;
        }

        if (predicate.Not is { } notChild
            && Evaluate(notChild, response)) return false;

        return true;
    }

    /// <summary>
    /// Walk the JSONPath clause: collect values at the given path from
    /// the parsed body, then run the operator (<c>exists</c> /
    /// <c>equals</c> / <c>matches</c> / <c>anyValueMatches</c>) over
    /// the result set.
    /// </summary>
    private static bool EvaluateJsonPath(AttackJsonPathClause clause, string body)
    {
        if (string.IsNullOrEmpty(body)) return clause.Exists == false;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return clause.Exists == false; }

        var matches = ResolvePath(root, clause.Path);

        if (clause.Exists is true) return matches.Count > 0;
        if (clause.Exists is false) return matches.Count == 0;

        if (matches.Count == 0) return false;

        if (!string.IsNullOrEmpty(clause.EqualsValue))
        {
            foreach (var m in matches)
            {
                if (string.Equals(Stringify(m), clause.EqualsValue, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        if (!string.IsNullOrEmpty(clause.Matches))
        {
            try
            {
                var joined = string.Join('\n', matches.ConvertAll(Stringify));
                return Regex.IsMatch(joined, clause.Matches,
                    RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (RegexMatchTimeoutException) { return false; }
            catch (ArgumentException) { return false; }
        }

        if (!string.IsNullOrEmpty(clause.AnyValueMatches))
        {
            Regex re;
            try { re = new Regex(clause.AnyValueMatches, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException) { return false; }
            foreach (var m in matches)
            {
                try { if (re.IsMatch(Stringify(m))) return true; }
                catch (RegexMatchTimeoutException) { /* try next value */ }
            }
            return false;
        }

        // No operator specified — treat as plain "the path resolves to
        // at least one match". Same semantics as `exists: true`.
        return true;
    }

    /// <summary>
    /// JSONPath subset walker — handles <c>$</c>, dotted property
    /// navigation, <c>[N]</c> array indexing, and <c>[*]</c> array
    /// wildcards. Returns every leaf the path resolves to (one for
    /// scalar paths, N for wildcard-expanded paths).
    /// </summary>
    private static List<JsonElement> ResolvePath(JsonElement root, string path)
    {
        var results = new List<JsonElement>();
        if (string.IsNullOrEmpty(path) || path == "$")
        {
            results.Add(root);
            return results;
        }
        var trimmed = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..]
                     : path.StartsWith('$') ? path[1..]
                     : path;

        Step(root, trimmed, 0, results);
        return results;
    }

    private static void Step(JsonElement node, string path, int i, List<JsonElement> sink)
    {
        if (i >= path.Length)
        {
            sink.Add(node);
            return;
        }

        if (path[i] == '.')
        {
            Step(node, path, i + 1, sink);
            return;
        }

        if (path[i] == '[')
        {
            var close = path.IndexOf(']', i);
            if (close < 0) return; // malformed
            var token = path[(i + 1)..close];
            if (token == "*")
            {
                if (node.ValueKind != JsonValueKind.Array) return;
                foreach (var item in node.EnumerateArray())
                {
                    Step(item, path, close + 1, sink);
                }
                return;
            }
            if (int.TryParse(token, out var idx))
            {
                if (node.ValueKind != JsonValueKind.Array) return;
                if (idx < 0 || idx >= node.GetArrayLength()) return;
                Step(node[idx], path, close + 1, sink);
                return;
            }
            return; // unsupported bracket token
        }

        // Property name — read up to next . or [
        var end = path.Length;
        for (var j = i; j < path.Length; j++)
        {
            if (path[j] == '.' || path[j] == '[') { end = j; break; }
        }
        var name = path[i..end];
        if (node.ValueKind != JsonValueKind.Object) return;
        if (!node.TryGetProperty(name, out var child)) return;
        Step(child, path, end, sink);
    }

    /// <summary>
    /// Convert a JsonElement leaf to a string for predicate comparison.
    /// Scalars render as their raw value (the C# Json string for
    /// strings, the literal for numbers / bools / null);
    /// objects / arrays render as their JSON text.
    /// </summary>
    private static string Stringify(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
        _ => "",
    };
}
