// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Kuestenlogik.Bowire.Mock.Replay;

/// <summary>
/// Dependency-free evaluators for the <c>${math:…}</c> and <c>${if:…}</c>
/// response-templating helpers (#430). Deliberately small: arithmetic over
/// <c>+ - * / %</c> with parens, and a ternary with the six comparison
/// operators (or a bare truthy value). Anything it can't parse is returned
/// verbatim so substitution stays idempotent.
/// </summary>
internal static class MockExpression
{
    /// <summary>Evaluate an arithmetic expression; returns the input on parse failure.</summary>
    public static string Math(string expr)
    {
        try
        {
            var parser = new Parser(expr);
            var value = parser.ParseExpression();
            parser.ExpectEnd();
            return Format(value);
        }
        catch (FormatException)
        {
            return expr;
        }
    }

    /// <summary>Evaluate <c>COND ? THEN : ELSE</c>; returns the input when it isn't a ternary.</summary>
    public static string If(string spec)
    {
        var q = spec.IndexOf('?', StringComparison.Ordinal);
        if (q < 0) return spec;
        var cond = spec[..q];
        var rest = spec[(q + 1)..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0) return spec;
        var thenPart = rest[..colon].Trim();
        var elsePart = rest[(colon + 1)..].Trim();
        return EvalCondition(cond) ? thenPart : elsePart;
    }

    // ---- condition ----

    private static bool EvalCondition(string cond)
    {
        var (a, op, b) = SplitComparison(cond);
        if (op is null)
        {
            // Bare truthiness.
            var v = cond.Trim();
            return v.Length > 0
                && !v.Equals("false", StringComparison.OrdinalIgnoreCase)
                && v != "0"
                && !v.Equals("null", StringComparison.OrdinalIgnoreCase);
        }

        var left = a.Trim();
        var right = b.Trim();
        double ln = 0, rn = 0;
        var numeric = TryNum(left, out ln) && TryNum(right, out rn);
        var cmp = numeric ? ln.CompareTo(rn) : string.CompareOrdinal(left, right);

        return op switch
        {
            "==" => numeric ? ln == rn : string.Equals(left, right, StringComparison.Ordinal),
            "!=" => numeric ? ln != rn : !string.Equals(left, right, StringComparison.Ordinal),
            "<" => cmp < 0,
            ">" => cmp > 0,
            "<=" => cmp <= 0,
            ">=" => cmp >= 0,
            _ => false,
        };
    }

    // Split on the first comparison operator (two-char first). Returns op=null when none.
    private static (string A, string? Op, string B) SplitComparison(string cond)
    {
        string[] twoChar = ["==", "!=", "<=", ">="];
        foreach (var op in twoChar)
        {
            var idx = cond.IndexOf(op, StringComparison.Ordinal);
            if (idx > 0) return (cond[..idx], op, cond[(idx + op.Length)..]);
        }
        foreach (var op in new[] { "<", ">" })
        {
            var idx = cond.IndexOf(op, StringComparison.Ordinal);
            if (idx > 0) return (cond[..idx], op, cond[(idx + 1)..]);
        }
        return (cond, null, "");
    }

    private static bool TryNum(string s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Format(double value)
    {
        if (System.Math.Abs(value % 1) < 1e-9 && System.Math.Abs(value) < 9.2e18)
            return ((long)System.Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        return value.ToString("0.############", CultureInfo.InvariantCulture);
    }

    // ---- arithmetic: recursive-descent over + - * / % and parens ----

    private ref struct Parser
    {
        private readonly ReadOnlySpan<char> _s;
        private int _pos;

        public Parser(string s) { _s = s.AsSpan(); _pos = 0; }

        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipWs();
                if (Peek('+')) { _pos++; value += ParseTerm(); }
                else if (Peek('-')) { _pos++; value -= ParseTerm(); }
                else break;
            }
            return value;
        }

        private double ParseTerm()
        {
            var value = ParseFactor();
            while (true)
            {
                SkipWs();
                if (Peek('*')) { _pos++; value *= ParseFactor(); }
                else if (Peek('/')) { _pos++; var d = ParseFactor(); value = d == 0 ? throw new FormatException("div by zero") : value / d; }
                else if (Peek('%')) { _pos++; var d = ParseFactor(); value = d == 0 ? throw new FormatException("mod by zero") : value % d; }
                else break;
            }
            return value;
        }

        private double ParseFactor()
        {
            SkipWs();
            if (Peek('-')) { _pos++; return -ParseFactor(); }
            if (Peek('+')) { _pos++; return ParseFactor(); }
            if (Peek('('))
            {
                _pos++;
                var value = ParseExpression();
                SkipWs();
                if (!Peek(')')) throw new FormatException("expected )");
                _pos++;
                return value;
            }
            return ParseNumber();
        }

        private double ParseNumber()
        {
            SkipWs();
            var start = _pos;
            while (_pos < _s.Length && (char.IsDigit(_s[_pos]) || _s[_pos] == '.')) _pos++;
            if (_pos == start) throw new FormatException("expected number");
            if (!double.TryParse(_s[start.._pos], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new FormatException("bad number");
            return value;
        }

        public void ExpectEnd()
        {
            SkipWs();
            if (_pos != _s.Length) throw new FormatException("trailing input");
        }

        private void SkipWs() { while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++; }
        private readonly bool Peek(char c) => _pos < _s.Length && _s[_pos] == c;
    }
}
