// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuestenlogik.Bowire.Paths;

/// <summary>A path that does not parse, with the position of the first character that does not fit.</summary>
public sealed class JsonPathException : Exception
{
    public JsonPathException() { }

    public JsonPathException(string message) : base(message) { }

    public JsonPathException(string message, Exception innerException) : base(message, innerException) { }

    public JsonPathException(string message, int position) : base(message) => Position = position;

    /// <summary>Zero-based position in the path.</summary>
    public int Position { get; }
}

/// <summary>
/// One node a path selected. <see cref="Value"/> is <c>null</c> for a JSON
/// <c>null</c>; a path that selects nothing returns no match at all, so
/// "missing" and "null" stay apart. <see cref="Parent"/> with
/// <see cref="Name"/> or <see cref="Index"/> says where the node sits, for
/// callers that write to it.
/// </summary>
public readonly record struct JsonPathMatch(JsonNode? Value, JsonNode? Parent, string? Name, int Index);

/// <summary>
/// The one path resolver of Bowire (#676): assertions, response chaining
/// and mock overrides, in the CLI and — through its JS mirror json-path.js,
/// held to it by a golden fixture — in the browser.
/// <para>Two spellings:</para>
/// <list type="bullet">
/// <item>The short form, as before: <c>items.0.tags.2</c>. Dots separate
/// names; a numeric name indexes an array and names a property of an
/// object.</item>
/// <item>JSONPath (RFC 9535 subset), starting with <c>$</c>: <c>.name</c>,
/// <c>['name']</c>, <c>[0]</c>, <c>[-1]</c>, <c>[*]</c>, <c>.*</c>,
/// <c>..name</c>, <c>[start:end:step]</c>, <c>[0,2]</c> and filters
/// <c>[?(@.done == true)]</c> with <c>== != &lt; &lt;= &gt; &gt;=</c>,
/// <c>&amp;&amp; || !</c>, parentheses and existence tests.</item>
/// </list>
/// </summary>
public static class BowireJsonPath
{
    private static readonly ConcurrentDictionary<string, Segment[]> s_cache = new(StringComparer.Ordinal);
    private const int CacheLimit = 512;

    /// <summary>Every node <paramref name="path"/> selects in <paramref name="root"/>, in document order.</summary>
    /// <exception cref="JsonPathException">The path does not parse.</exception>
    public static IReadOnlyList<JsonPathMatch> Select(JsonNode? root, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = Compile(path);
        List<JsonPathMatch> current = [new JsonPathMatch(root, null, null, -1)];
        foreach (var segment in segments)
        {
            var next = new List<JsonPathMatch>();
            foreach (var match in current) segment.Apply(match, root, next);
            current = next;
        }
        return current;
    }

    /// <summary>
    /// The value an assertion or a chaining placeholder sees: nothing when
    /// the path selects nothing, the node itself for one match, and an array
    /// of the matches when a wildcard or filter selects several.
    /// </summary>
    /// <exception cref="JsonPathException">The path does not parse.</exception>
    public static bool TryResolve(JsonNode? root, string path, out JsonNode? value)
    {
        var matches = Select(root, path);
        switch (matches.Count)
        {
            case 0:
                value = null;
                return false;
            case 1 when !IsMultiple(path):
                value = matches[0].Value;
                return true;
            default:
                value = new JsonArray(matches.Select(m => m.Value?.DeepClone()).ToArray());
                return true;
        }
    }

    /// <summary>Whether a path can select more than one node — a wildcard, slice, union, filter or descent.</summary>
    public static bool IsMultiple(string path) => Compile(path).Any(s => s.Multiple);

    /// <summary>Whether a path is plain names and indices, the shape a writer can create missing parents for.</summary>
    public static bool IsSimple(string path) => Compile(path).All(s => s is NameSegment or IndexSegment or NameOrIndexSegment);

    /// <summary>The plain steps of a simple path: a name, or an index (as its digits) for an array.</summary>
    public static IReadOnlyList<string> SimpleSteps(string path) => Compile(path).Select(s => s switch
    {
        NameSegment n => n.Name,
        IndexSegment i => i.Index.ToString(CultureInfo.InvariantCulture),
        NameOrIndexSegment n => n.Name,
        _ => throw new JsonPathException($"'{path}' is not a plain path of names and indices", 0),
    }).ToList();

    private static Segment[] Compile(string path)
    {
        if (s_cache.TryGetValue(path, out var cached)) return cached;
        var segments = new Parser(path).ParsePath();
        if (s_cache.Count < CacheLimit) s_cache.TryAdd(path, segments);
        return segments;
    }

    // ---- segments ----

    private abstract class Segment
    {
        public virtual bool Multiple => false;
        public abstract void Apply(JsonPathMatch match, JsonNode? root, List<JsonPathMatch> output);
    }

    private sealed class NameSegment(string name) : Segment
    {
        public string Name { get; } = name;

        public override void Apply(JsonPathMatch match, JsonNode? root, List<JsonPathMatch> output)
        {
            if (match.Value is JsonObject obj && obj.TryGetPropertyValue(Name, out var child))
                output.Add(new JsonPathMatch(child, obj, Name, -1));
        }
    }

    private sealed class IndexSegment(int index) : Segment
    {
        public int Index { get; } = index;

        public override void Apply(JsonPathMatch match, JsonNode? root, List<JsonPathMatch> output)
        {
            if (match.Value is not JsonArray array) return;
            var i = Index < 0 ? array.Count + Index : Index;
            if (i >= 0 && i < array.Count) output.Add(new JsonPathMatch(array[i], array, null, i));
        }
    }

    // The short form's numeric step: an index into an array, a name on an object.
    private sealed class NameOrIndexSegment(string name) : Segment
    {
        public string Name { get; } = name;

        public override void Apply(JsonPathMatch match, JsonNode? root, List<JsonPathMatch> output)
        {
            if (match.Value is JsonArray array)
            {
                if (int.TryParse(Name, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < array.Count)
                    output.Add(new JsonPathMatch(array[i], array, null, i));
            }
            else if (match.Value is JsonObject obj && obj.TryGetPropertyValue(Name, out var child))
            {
                output.Add(new JsonPathMatch(child, obj, Name, -1));
            }
        }
    }

    private sealed class WildcardSegment : Segment
    {
        public override bool Multiple => true;

        public override void Apply(JsonPathMatch match, JsonNode? root, List<JsonPathMatch> output) => Children(match, output);
    }

    private sealed class SliceSegment(int? start, int? end, int step) : Segment
    {
        public override bool Multiple => true;

        public override void Apply(JsonPathMatch match, JsonNode? root, List<JsonPathMatch> output)
        {
            if (match.Value is not JsonArray array || step == 0) return;
            var len = array.Count;
            int Normalize(int v) => v >= 0 ? v : len + v;
            if (step > 0)
            {
                var lower = Math.Min(Math.Max(Normalize(start ?? 0), 0), len);
                var upper = Math.Min(Math.Max(Normalize(end ?? len), 0), len);
                for (var i = lower; i < upper; i += step) output.Add(new JsonPathMatch(array[i], array, null, i));
            }
            else
            {
                var upper = Math.Min(Math.Max(Normalize(start ?? len - 1), -1), len - 1);
                var lower = Math.Min(Math.Max(Normalize(end ?? -len - 1), -1), len - 1);
                for (var i = upper; i > lower; i += step) output.Add(new JsonPathMatch(array[i], array, null, i));
            }
        }
    }

    private sealed class UnionSegment(Segment[] parts) : Segment
    {
        public override bool Multiple => true;

        public override void Apply(JsonPathMatch match, JsonNode? root, List<JsonPathMatch> output)
        {
            foreach (var part in parts) part.Apply(match, root, output);
        }
    }

    private sealed class FilterSegment(Expr filter) : Segment
    {
        public override bool Multiple => true;

        public override void Apply(JsonPathMatch match, JsonNode? root, List<JsonPathMatch> output)
        {
            var children = new List<JsonPathMatch>();
            Children(match, children);
            foreach (var child in children)
            {
                if (filter.Test(child.Value, root)) output.Add(child);
            }
        }
    }

    // `..x`: the inner segment applied to the node and every descendant, in document order.
    private sealed class DescendantSegment(Segment inner) : Segment
    {
        public override bool Multiple => true;

        public override void Apply(JsonPathMatch match, JsonNode? root, List<JsonPathMatch> output)
        {
            inner.Apply(match, root, output);
            var children = new List<JsonPathMatch>();
            Children(match, children);
            foreach (var child in children)
            {
                if (child.Value is JsonObject or JsonArray) Apply(child, root, output);
            }
        }
    }

    private static void Children(JsonPathMatch match, List<JsonPathMatch> output)
    {
        switch (match.Value)
        {
            case JsonArray array:
                for (var i = 0; i < array.Count; i++) output.Add(new JsonPathMatch(array[i], array, null, i));
                break;
            case JsonObject obj:
                foreach (var (key, value) in obj) output.Add(new JsonPathMatch(value, obj, key, -1));
                break;
        }
    }

    // ---- filter expressions ----

    // An operand's value: Nothing when a path selects nothing, else a JSON value (null for JSON null).
    private readonly record struct Operand(bool Nothing, JsonNode? Node);

    private abstract class Expr
    {
        public abstract bool Test(JsonNode? current, JsonNode? root);
    }

    private sealed class OrExpr(Expr left, Expr right) : Expr
    {
        public override bool Test(JsonNode? current, JsonNode? root) => left.Test(current, root) || right.Test(current, root);
    }

    private sealed class AndExpr(Expr left, Expr right) : Expr
    {
        public override bool Test(JsonNode? current, JsonNode? root) => left.Test(current, root) && right.Test(current, root);
    }

    private sealed class NotExpr(Expr inner) : Expr
    {
        public override bool Test(JsonNode? current, JsonNode? root) => !inner.Test(current, root);
    }

    private sealed class ExistsExpr(QueryOperand query) : Expr
    {
        public override bool Test(JsonNode? current, JsonNode? root) => query.Matches(current, root).Count > 0;
    }

    private sealed class CompareExpr(OperandExpr left, string op, OperandExpr right) : Expr
    {
        public override bool Test(JsonNode? current, JsonNode? root)
        {
            var a = left.Evaluate(current, root);
            var b = right.Evaluate(current, root);
            return op switch
            {
                "==" => AreEqual(a, b),
                "!=" => !AreEqual(a, b),
                "<" => Less(a, b),
                "<=" => Less(a, b) || AreEqual(a, b),
                ">" => Less(b, a),
                ">=" => Less(b, a) || AreEqual(a, b),
                _ => false,
            };
        }
    }

    private abstract class OperandExpr
    {
        public abstract Operand Evaluate(JsonNode? current, JsonNode? root);
    }

    private sealed class LiteralOperand(JsonNode? node) : OperandExpr
    {
        public override Operand Evaluate(JsonNode? current, JsonNode? root) => new(false, node);
    }

    private sealed class QueryOperand(bool relative, Segment[] segments) : OperandExpr
    {
        public List<JsonPathMatch> Matches(JsonNode? current, JsonNode? root)
        {
            List<JsonPathMatch> nodes = [new JsonPathMatch(relative ? current : root, null, null, -1)];
            foreach (var segment in segments)
            {
                var next = new List<JsonPathMatch>();
                foreach (var node in nodes) segment.Apply(node, root, next);
                nodes = next;
            }
            return nodes;
        }

        // A comparison takes a single node; none or several compare as Nothing.
        public override Operand Evaluate(JsonNode? current, JsonNode? root)
        {
            var matches = Matches(current, root);
            return matches.Count == 1 ? new Operand(false, matches[0].Value) : new Operand(true, null);
        }
    }

    private static bool AreEqual(Operand a, Operand b)
    {
        if (a.Nothing || b.Nothing) return a.Nothing && b.Nothing;
        if (a.Node is null || b.Node is null) return a.Node is null && b.Node is null;
        var ka = a.Node.GetValueKind();
        var kb = b.Node.GetValueKind();
        if (ka == JsonValueKind.Number && kb == JsonValueKind.Number) return Number(a.Node) == Number(b.Node);
        return JsonNode.DeepEquals(a.Node, b.Node);
    }

    // Only numbers with numbers and strings with strings are ordered.
    private static bool Less(Operand a, Operand b)
    {
        if (a.Nothing || b.Nothing || a.Node is null || b.Node is null) return false;
        var ka = a.Node.GetValueKind();
        var kb = b.Node.GetValueKind();
        if (ka == JsonValueKind.Number && kb == JsonValueKind.Number) return Number(a.Node) < Number(b.Node);
        if (ka == JsonValueKind.String && kb == JsonValueKind.String)
            return string.CompareOrdinal(a.Node.GetValue<string>(), b.Node.GetValue<string>()) < 0;
        return false;
    }

    private static double Number(JsonNode node) => node.GetValue<double>();

    // ---- parser ----

    private sealed class Parser(string text)
    {
        private int _pos;

        public Segment[] ParsePath()
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0) return [];
            if (text.TrimStart().StartsWith('$'))
            {
                _pos = text.IndexOf('$', StringComparison.Ordinal) + 1;
                var segments = ParseSegments(inFilter: false);
                SkipSpace();
                if (_pos < text.Length) throw Error("unexpected character");
                return segments;
            }
            return ParseShort();
        }

        // `items.0.tags` and `items[0]['name']`: the form assertions always took.
        private Segment[] ParseShort()
        {
            var segments = new List<Segment>();
            var name = new StringBuilder();
            void Flush()
            {
                if (name.Length == 0) return;
                var s = name.ToString();
                segments.Add(s.All(char.IsAsciiDigit) ? new NameOrIndexSegment(s) : new NameSegment(s));
                name.Clear();
            }
            while (_pos < text.Length)
            {
                var c = text[_pos];
                if (c == '.')
                {
                    Flush();
                    _pos++;
                }
                else if (c == '[')
                {
                    Flush();
                    segments.Add(ParseBracket(inFilter: false));
                }
                else
                {
                    name.Append(c);
                    _pos++;
                }
            }
            Flush();
            return [.. segments];
        }

        private Segment[] ParseSegments(bool inFilter)
        {
            var segments = new List<Segment>();
            while (true)
            {
                if (inFilter) SkipSpace();
                if (_pos >= text.Length) break;
                if (Peek("..") )
                {
                    _pos += 2;
                    if (_pos < text.Length && text[_pos] == '[') segments.Add(new DescendantSegment(ParseBracket(inFilter)));
                    else if (_pos < text.Length && text[_pos] == '*') { _pos++; segments.Add(new DescendantSegment(new WildcardSegment())); }
                    else segments.Add(new DescendantSegment(new NameSegment(ParseName())));
                }
                else if (text[_pos] == '.')
                {
                    _pos++;
                    if (_pos < text.Length && text[_pos] == '*') { _pos++; segments.Add(new WildcardSegment()); }
                    else
                    {
                        // `$.tags.0`: a numeric step indexes an array, as in the short form.
                        var name = ParseName();
                        segments.Add(name.All(char.IsAsciiDigit) ? new NameOrIndexSegment(name) : new NameSegment(name));
                    }
                }
                else if (text[_pos] == '[')
                {
                    segments.Add(ParseBracket(inFilter));
                }
                else
                {
                    break;
                }
            }
            return [.. segments];
        }

        private string ParseName()
        {
            var start = _pos;
            while (_pos < text.Length && IsNameChar(text[_pos])) _pos++;
            if (_pos == start) throw Error("expected a name");
            return text[start.._pos];
        }

        private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '$' || c > 127;

        private Segment ParseBracket(bool inFilter)
        {
            Expect('[');
            SkipSpace();
            if (Peek("?"))
            {
                _pos++;
                SkipSpace();
                var expr = ParseOr();
                SkipSpace();
                Expect(']');
                return new FilterSegment(expr);
            }
            var parts = new List<Segment>();
            while (true)
            {
                SkipSpace();
                parts.Add(ParseSelector());
                SkipSpace();
                if (_pos < text.Length && text[_pos] == ',') { _pos++; continue; }
                break;
            }
            Expect(']');
            return parts.Count == 1 ? parts[0] : new UnionSegment([.. parts]);
        }

        private Segment ParseSelector()
        {
            if (_pos >= text.Length) throw Error("expected a selector");
            var c = text[_pos];
            if (c == '*') { _pos++; return new WildcardSegment(); }
            if (c is '\'' or '"') return new NameSegment(ParseString());
            if (c == ':' || c == '-' || char.IsAsciiDigit(c))
            {
                int? start = c == ':' ? null : ParseInt();
                SkipSpace();
                if (_pos < text.Length && text[_pos] == ':')
                {
                    _pos++;
                    SkipSpace();
                    int? end = _pos < text.Length && (text[_pos] == '-' || char.IsAsciiDigit(text[_pos])) ? ParseInt() : null;
                    SkipSpace();
                    var step = 1;
                    if (_pos < text.Length && text[_pos] == ':')
                    {
                        _pos++;
                        SkipSpace();
                        if (_pos < text.Length && (text[_pos] == '-' || char.IsAsciiDigit(text[_pos]))) step = ParseInt();
                    }
                    return new SliceSegment(start, end, step);
                }
                return new IndexSegment(start ?? 0);
            }
            throw Error("expected a name in quotes, an index, a slice, '*' or a filter '?'");
        }

        // ---- filter grammar ----

        private Expr ParseOr()
        {
            var left = ParseAnd();
            while (true)
            {
                SkipSpace();
                if (!Peek("||")) return left;
                _pos += 2;
                left = new OrExpr(left, ParseAnd());
            }
        }

        private Expr ParseAnd()
        {
            var left = ParseUnary();
            while (true)
            {
                SkipSpace();
                if (!Peek("&&")) return left;
                _pos += 2;
                left = new AndExpr(left, ParseUnary());
            }
        }

        private Expr ParseUnary()
        {
            SkipSpace();
            if (Peek("!") && !Peek("!="))
            {
                _pos++;
                return new NotExpr(ParseUnary());
            }
            if (Peek("("))
            {
                _pos++;
                var inner = ParseOr();
                SkipSpace();
                Expect(')');
                return inner;
            }
            var operandStart = _pos;
            var left = ParseOperand();
            SkipSpace();
            foreach (var op in (string[])["==", "!=", "<=", ">=", "<", ">"])
            {
                if (!Peek(op)) continue;
                _pos += op.Length;
                SkipSpace();
                return new CompareExpr(left, op, ParseOperand());
            }
            if (left is QueryOperand query) return new ExistsExpr(query);
            throw new JsonPathException($"Invalid JSONPath at position {operandStart}: a literal on its own is not a test; compare it with == or !=", operandStart);
        }

        private OperandExpr ParseOperand()
        {
            SkipSpace();
            if (_pos >= text.Length) throw Error("expected a value or a path");
            var c = text[_pos];
            if (c is '@' or '$')
            {
                _pos++;
                return new QueryOperand(c == '@', ParseSegments(inFilter: false));
            }
            if (c is '\'' or '"') return new LiteralOperand(JsonValue.Create(ParseString()));
            if (c == '-' || char.IsAsciiDigit(c)) return new LiteralOperand(ParseNumber());
            if (Peek("true")) { _pos += 4; return new LiteralOperand(JsonValue.Create(true)); }
            if (Peek("false")) { _pos += 5; return new LiteralOperand(JsonValue.Create(false)); }
            if (Peek("null")) { _pos += 4; return new LiteralOperand(null); }
            throw Error("expected a value or a path");
        }

        private JsonValue ParseNumber()
        {
            var start = _pos;
            if (text[_pos] == '-') _pos++;
            while (_pos < text.Length && (char.IsAsciiDigit(text[_pos]) || text[_pos] is '.' or 'e' or 'E' or '+' or '-')) _pos++;
            var raw = text[start.._pos];
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                throw new JsonPathException($"Invalid JSONPath at position {start}: '{raw}' is not a number", start);
            return JsonValue.Create(number);
        }

        private int ParseInt()
        {
            var start = _pos;
            if (text[_pos] == '-') _pos++;
            while (_pos < text.Length && char.IsAsciiDigit(text[_pos])) _pos++;
            if (!int.TryParse(text.AsSpan(start, _pos - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                throw new JsonPathException($"Invalid JSONPath at position {start}: expected an integer", start);
            return value;
        }

        private string ParseString()
        {
            var quote = text[_pos];
            var start = _pos;
            _pos++;
            var sb = new StringBuilder();
            while (_pos < text.Length && text[_pos] != quote)
            {
                if (text[_pos] == '\\' && _pos + 1 < text.Length)
                {
                    _pos++;
                    sb.Append(text[_pos] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', var other => other });
                }
                else
                {
                    sb.Append(text[_pos]);
                }
                _pos++;
            }
            if (_pos >= text.Length) throw new JsonPathException($"Invalid JSONPath at position {start}: unterminated string", start);
            _pos++;
            return sb.ToString();
        }

        private void SkipSpace()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }

        private bool Peek(string s) => string.CompareOrdinal(text, _pos, s, 0, s.Length) == 0;

        private void Expect(char c)
        {
            if (_pos < text.Length && text[_pos] == c)
            {
                _pos++;
                return;
            }
            throw Error($"expected '{c}'");
        }

        private JsonPathException Error(string what) =>
            new($"Invalid JSONPath at position {_pos}: {what}", _pos);
    }
}
