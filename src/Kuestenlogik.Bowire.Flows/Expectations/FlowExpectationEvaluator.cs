// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Flows.Expectations;

/// <summary>
/// Pure-function evaluator: given a <see cref="FlowExpectation"/> and a
/// <see cref="FlowRequestEnvelope"/>, return a <see cref="FlowExpectationResult"/>.
/// No I/O, no global state — so the v2.2 CLI runner (T2) and the in-process
/// flow runner share the same predicate semantics.
/// </summary>
public static class FlowExpectationEvaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Evaluate one expectation. Always returns a result — even when the
    /// envelope is empty or the path doesn't resolve — because the runner's
    /// soft-fail semantics need a row per expectation regardless of
    /// success.
    /// </summary>
    public static FlowExpectationResult Evaluate(FlowExpectation expectation, FlowRequestEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(envelope);

        var (actual, actualText) = ResolveActual(expectation, envelope);
        var passed = ApplyOperator(expectation.Operator, actual, actualText, expectation.Expected);
        var message = FormatMessage(expectation, actualText, passed);

        return new FlowExpectationResult
        {
            Id = expectation.Id,
            Kind = expectation.Kind,
            Operator = expectation.Operator,
            Passed = passed,
            Actual = actualText,
            Expected = expectation.Expected,
            Message = message,
        };
    }

    /// <summary>
    /// Evaluate every expectation on one step. The result preserves
    /// declaration order so the UI can render rows in the same sequence
    /// the operator wrote them.
    /// </summary>
    public static FlowStepExpectationResult EvaluateStep(
        string stepId,
        IEnumerable<FlowExpectation> expectations,
        FlowRequestEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(expectations);
        var rows = new List<FlowExpectationResult>();
        var passed = 0;
        var failed = 0;
        foreach (var exp in expectations)
        {
            var row = Evaluate(exp, envelope);
            rows.Add(row);
            if (row.Passed) passed++; else failed++;
        }
        return new FlowStepExpectationResult
        {
            StepId = stepId,
            Passed = passed,
            Failed = failed,
            Evaluations = rows,
        };
    }

    // ---- Actual resolution per kind ----

    private static (object? Value, string Text) ResolveActual(FlowExpectation expectation, FlowRequestEnvelope envelope)
    {
        switch (expectation.Kind)
        {
            case FlowExpectationKind.Status:
            {
                var text = envelope.Status ?? string.Empty;
                return (text, text);
            }
            case FlowExpectationKind.Latency:
            {
                return (envelope.LatencyMs, envelope.LatencyMs.ToString(CultureInfo.InvariantCulture));
            }
            case FlowExpectationKind.Header:
            {
                var name = expectation.Target ?? string.Empty;
                if (envelope.Headers.TryGetValue(name, out var value))
                {
                    return (value, value);
                }
                return (null, string.Empty);
            }
            case FlowExpectationKind.BodyText:
            {
                var text = envelope.Body ?? string.Empty;
                return (text, text);
            }
            case FlowExpectationKind.BodyPath:
            {
                var node = WalkJsonPath(envelope.Body, expectation.Target ?? string.Empty);
                if (node is null) return (null, string.Empty);
                if (node is JsonValue jv && jv.TryGetValue<string>(out var s)) return (s, s);
                return (node, node.ToJsonString());
            }
            default:
                return (null, string.Empty);
        }
    }

    /// <summary>
    /// Walk a dotted / $-anchored path against a (possibly null) JSON body.
    /// Returns null when the body isn't parseable or the path doesn't
    /// resolve — operators decide whether that's a pass or a fail.
    /// </summary>
    private static JsonNode? WalkJsonPath(string? body, string path)
    {
        if (string.IsNullOrEmpty(body)) return null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
        if (root is null) return null;

        var trimmed = path.Trim();
        if (trimmed.Length == 0 || trimmed == "$") return root;
        if (trimmed.StartsWith("$.", StringComparison.Ordinal)) trimmed = trimmed.Substring(2);
        else if (trimmed[0] == '$') trimmed = trimmed.Substring(1);
        if (trimmed.StartsWith("response.", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring("response.".Length);
        }

        if (trimmed.Length == 0) return root;

        var current = root;
        foreach (var segment in trimmed.Split('.'))
        {
            if (current is null) return null;
            if (current is JsonArray arr)
            {
                if (!int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)) return null;
                if (idx < 0 || idx >= arr.Count) return null;
                current = arr[idx];
            }
            else if (current is JsonObject obj)
            {
                current = obj[segment];
            }
            else
            {
                return null;
            }
        }
        return current;
    }

    // ---- Operator application ----

    private static bool ApplyOperator(FlowExpectationOperator op, object? actual, string actualText, string? expected)
    {
        return op switch
        {
            FlowExpectationOperator.Exists => actual is not null,
            FlowExpectationOperator.NotExists => actual is null,
            FlowExpectationOperator.Equals => EqualsLoose(actual, actualText, expected),
            FlowExpectationOperator.NotEquals => !EqualsLoose(actual, actualText, expected),
            FlowExpectationOperator.Contains => Contains(actual, actualText, expected),
            FlowExpectationOperator.GreaterThan => CompareNumbers(actual, actualText, expected, static (a, e) => a > e),
            FlowExpectationOperator.GreaterThanOrEquals => CompareNumbers(actual, actualText, expected, static (a, e) => a >= e),
            FlowExpectationOperator.LessThan => CompareNumbers(actual, actualText, expected, static (a, e) => a < e),
            FlowExpectationOperator.LessThanOrEquals => CompareNumbers(actual, actualText, expected, static (a, e) => a <= e),
            FlowExpectationOperator.Regex => MatchesRegex(actualText, expected),
            _ => false,
        };
    }

    private static bool EqualsLoose(object? actual, string actualText, string? expected)
    {
        if (actual is null && string.IsNullOrEmpty(expected)) return true;
        if (actual is null) return false;
        if (expected is null) return false;

        if (TryNumber(actual, actualText, out var a) && TryNumber(expected, expected, out var e))
        {
            var diff = Math.Abs(a - e);
            var scale = Math.Max(Math.Abs(a), Math.Abs(e));
            return diff <= Math.Max(1e-9, scale * 1e-9);
        }

        if (string.Equals(expected, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(expected, "false", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(actualText, expected, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(actualText, expected, StringComparison.Ordinal);
    }

    private static bool Contains(object? actual, string actualText, string? expected)
    {
        if (expected is null) return false;
        if (actual is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var itemText = item is null ? string.Empty : item.ToJsonString();
                if (EqualsLoose(item, itemText, expected)) return true;
                // strings inside arrays are quoted by ToJsonString; compare unquoted too
                if (item is JsonValue jv && jv.TryGetValue<string>(out var s) && string.Equals(s, expected, StringComparison.Ordinal)) return true;
            }
            return false;
        }
        return actualText.Contains(expected, StringComparison.Ordinal);
    }

    private static bool CompareNumbers(object? actual, string actualText, string? expected, Func<double, double, bool> predicate)
    {
        if (expected is null) return false;
        if (!TryNumber(actual, actualText, out var a)) return false;
        if (!TryNumber(expected, expected, out var e)) return false;
        return predicate(a, e);
    }

    private static bool MatchesRegex(string actualText, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return false;
        try
        {
            return Regex.IsMatch(actualText, expected, RegexOptions.None, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    [SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Symmetry with EqualsLoose's signature so callers can pass both the live object and its text representation.")]
    private static bool TryNumber(object? value, string text, out double n)
    {
        n = 0;
        if (value is null && string.IsNullOrEmpty(text)) return false;
        if (value is double dn) { n = dn; return true; }
        if (value is long ln) { n = ln; return true; }
        if (value is int @in) { n = @in; return true; }
        if (value is JsonValue jv && jv.TryGetValue(out double d)) { n = d; return true; }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out n);
    }

    // ---- Human-readable summary ----

    private static string FormatMessage(FlowExpectation expectation, string actualText, bool passed)
    {
        var verb = passed ? "matched" : "failed";
        var subject = expectation.Kind switch
        {
            FlowExpectationKind.Status => "status",
            FlowExpectationKind.Latency => "latency",
            FlowExpectationKind.Header => $"header[{expectation.Target}]",
            FlowExpectationKind.BodyText => "body",
            FlowExpectationKind.BodyPath => expectation.Target ?? "$",
            _ => "?",
        };
        var op = OperatorLabel(expectation.Operator);
        var rhs = expectation.Operator switch
        {
            FlowExpectationOperator.Exists or FlowExpectationOperator.NotExists => string.Empty,
            _ => " " + (expectation.Expected ?? string.Empty),
        };
        return passed
            ? $"{subject} {op}{rhs} — {verb}"
            : $"{subject} {op}{rhs} — {verb} (actual: {Trunc(actualText)})";
    }

    private static string OperatorLabel(FlowExpectationOperator op) => op switch
    {
        FlowExpectationOperator.Equals => "equals",
        FlowExpectationOperator.NotEquals => "not-equals",
        FlowExpectationOperator.Contains => "contains",
        FlowExpectationOperator.LessThan => "<",
        FlowExpectationOperator.LessThanOrEquals => "<=",
        FlowExpectationOperator.GreaterThan => ">",
        FlowExpectationOperator.GreaterThanOrEquals => ">=",
        FlowExpectationOperator.Exists => "exists",
        FlowExpectationOperator.NotExists => "not-exists",
        FlowExpectationOperator.Regex => "matches",
        _ => "?",
    };

    private static string Trunc(string s) => s.Length <= 60 ? s : string.Concat(s.AsSpan(0, 57), "…");
}
