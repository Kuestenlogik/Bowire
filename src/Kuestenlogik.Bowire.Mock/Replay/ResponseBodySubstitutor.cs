// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Kuestenlogik.Bowire.Mock.Replay;

/// <summary>
/// Substitutes dynamic placeholders inside a recorded response body at replay
/// time, using Bowire's canonical <c>{{name}}</c> variable syntax (Postman /
/// v2.2 — same delimiter as the workbench variable resolver and WireMock's
/// Handlebars templating).
/// </summary>
/// <remarks>
/// <para>Value tokens (leaf generators):</para>
/// <list type="bullet">
/// <item><c>{{uuid}}</c> — fresh UUID v4 per substitution.</item>
/// <item><c>{{now}}</c> / <c>{{nowMs}}</c> — current Unix timestamp (s / ms).</item>
/// <item><c>{{now+N}}</c> / <c>{{now-N}}</c> — <c>{{now}}</c> shifted by <c>N</c> seconds.</item>
/// <item><c>{{timestamp}}</c> — current UTC time as ISO 8601.</item>
/// <item><c>{{random}}</c> — random <c>uint32</c>.</item>
/// <item><c>{{faker.*}}</c> — fake data (name / email / int(a,b) / lorem(n) / …).</item>
/// <item><c>{{request.*}}</c> — inbound-request values (see <see cref="RequestTemplate"/>).</item>
/// </list>
/// <para>Expression helpers (#430) — the argument is substituted first, so
/// helpers can nest value tokens:</para>
/// <list type="bullet">
/// <item><c>{{math:EXPR}}</c> — arithmetic over <c>+ - * / %</c> and parens,
/// e.g. <c>{{math:{{request.body.qty}} * 2}}</c>.</item>
/// <item><c>{{if:A OP B ? THEN : ELSE}}</c> — ternary; <c>OP</c> is
/// <c>== != &lt; &gt; &lt;= &gt;=</c>, or a bare truthy value.</item>
/// <item><c>{{upper:X}}</c> / <c>{{lower:X}}</c> — case folding.</item>
/// <item><c>{{default:X|Y}}</c> — <c>X</c> unless empty, else <c>Y</c>.</item>
/// </list>
/// <para><c>{{{{name}}}}</c> escapes to a literal <c>{{name}}</c>. Unknown
/// tokens are left verbatim. Loops / block templates aren't supported by this
/// flat syntax (tracked as a follow-up).</para>
/// <para><strong>Deprecated:</strong> the legacy <c>${name}</c> dollar syntax is
/// still resolved (leaf tokens only, no helpers) for recordings authored before
/// the move to <c>{{name}}</c>, but it is deprecated and slated for removal in
/// v3.0 — author new recordings with <c>{{name}}</c>.</para>
/// </remarks>
public static class ResponseBodySubstitutor
{
    /// <inheritdoc cref="Substitute(string, RequestTemplate?, IReadOnlyDictionary{string, string}?)"/>
    public static string Substitute(string body) => Substitute(body, request: null, extraBindings: null);

    /// <inheritdoc cref="Substitute(string, RequestTemplate?, IReadOnlyDictionary{string, string}?)"/>
    public static string Substitute(string body, IReadOnlyDictionary<string, string>? extraBindings) =>
        Substitute(body, request: null, extraBindings);

    /// <summary>
    /// Apply placeholder substitution to <paramref name="body"/>. Built-ins
    /// (uuid/now/faker/…) → request tokens → extra bindings → literal fallback;
    /// built-ins win so a hostile extra-binding can't spoof a generator.
    /// </summary>
    public static string Substitute(
        string body,
        RequestTemplate? request,
        IReadOnlyDictionary<string, string>? extraBindings)
    {
        if (string.IsNullOrEmpty(body)) return body;

        var result = body;
        if (result.Contains("{{", StringComparison.Ordinal))
            result = ScanCurly(result, request, extraBindings);
        // Deprecated dollar syntax — resolved after the canonical pass for
        // back-compat with pre-{{}} recordings.
        if (result.Contains("${", StringComparison.Ordinal))
            result = ScanDollar(result, request, extraBindings);
        return result;
    }

    // ---- canonical {{ ... }} (nesting-aware, escape, helpers) ----

    private static string ScanCurly(
        string s, RequestTemplate? request, IReadOnlyDictionary<string, string>? extra)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var open = s.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, open - i);

            // Escape: {{{{name}}}} → literal {{name}}.
            if (StartsWith(s, open, "{{{{"))
            {
                var escClose = s.IndexOf("}}}}", open + 4, StringComparison.Ordinal);
                if (escClose >= 0)
                {
                    sb.Append("{{").Append(s, open + 4, escClose - (open + 4)).Append("}}");
                    i = escClose + 4;
                    continue;
                }
            }

            var close = FindMatchingCurlyClose(s, open + 2);
            if (close < 0) { sb.Append(s, open, s.Length - open); break; }

            var inner = s.Substring(open + 2, close - (open + 2));
            sb.Append(ResolveToken(inner.Trim(), request, extra));
            i = close + 2;
        }
        return sb.ToString();
    }

    // Index just past the '}}' that closes a '{{' whose content starts at
    // contentStart, counting nested {{ }}. -1 when unbalanced. Returns the
    // index of the first '}' of the closing '}}'.
    private static int FindMatchingCurlyClose(string s, int contentStart)
    {
        var depth = 1;
        var j = contentStart;
        while (j < s.Length)
        {
            if (StartsWith(s, j, "{{")) { depth++; j += 2; }
            else if (StartsWith(s, j, "}}")) { depth--; if (depth == 0) return j; j += 2; }
            else j++;
        }
        return -1;
    }

    private static bool StartsWith(string s, int at, string token) =>
        at + token.Length <= s.Length && string.CompareOrdinal(s, at, token, 0, token.Length) == 0;

    private static string SubstituteArg(
        string arg, RequestTemplate? request, IReadOnlyDictionary<string, string>? extra)
        => arg.Contains("{{", StringComparison.Ordinal) ? ScanCurly(arg, request, extra) : arg;

    private static string ResolveToken(
        string inner, RequestTemplate? request, IReadOnlyDictionary<string, string>? extra)
    {
        var colon = inner.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            var head = inner[..colon].Trim();
            if (IsHelper(head))
            {
                var arg = SubstituteArg(inner[(colon + 1)..], request, extra);
                return EvalHelper(head, arg);
            }
        }
        return ResolveSimple(inner, request, extra) ?? "{{" + inner + "}}";
    }

    // ---- deprecated ${ ... } (flat leaf tokens only, no helpers) ----

    private static string ScanDollar(
        string s, RequestTemplate? request, IReadOnlyDictionary<string, string>? extra)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var open = s.IndexOf("${", i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, open - i);

            var close = s.IndexOf('}', open + 2);
            if (close < 0) { sb.Append(s, open, s.Length - open); break; }

            var inner = s.Substring(open + 2, close - (open + 2));
            // ${$name} escape → literal ${name}.
            if (inner.StartsWith('$'))
                sb.Append("${").Append(inner, 1, inner.Length - 1).Append('}');
            else
                // Deprecated path: leaf tokens only (helpers are {{…}}-exclusive).
                sb.Append(ResolveSimple(inner, request, extra) ?? "${" + inner + "}");
            i = close + 1;
        }
        return sb.ToString();
    }

    // ---- helpers ----

    private static bool IsHelper(string head) =>
        head is "math" or "if" or "upper" or "lower" or "default";

    private static string EvalHelper(string head, string arg) => head switch
    {
        "upper" => arg.ToUpperInvariant(),
        "lower" => Lower(arg),
        "default" => Default(arg),
        "math" => MockExpression.Math(arg),
        "if" => MockExpression.If(arg),
        _ => arg,
    };

    private static string Default(string arg)
    {
        var bar = arg.IndexOf('|', StringComparison.Ordinal);
        if (bar < 0) return arg;
        var primary = arg[..bar];
        return string.IsNullOrEmpty(primary) ? arg[(bar + 1)..] : primary;
    }

    private static string Lower(string s) =>
        string.Create(s.Length, s, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++) span[i] = char.ToLowerInvariant(src[i]);
        });

    // Resolve a leaf token; null = unknown (caller keeps the literal).
    private static string? ResolveSimple(
        string token, RequestTemplate? request, IReadOnlyDictionary<string, string>? extraBindings)
    {
        return token switch
        {
            "uuid" => Guid.NewGuid().ToString(),
            "now" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "nowMs" => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            "timestamp" => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            "random" => RandomUInt32().ToString(CultureInfo.InvariantCulture),
            _ when token.StartsWith("faker.", StringComparison.Ordinal) => MockFaker.Generate(token[6..]),
            _ when token.StartsWith("now+", StringComparison.Ordinal) => NowOffset(token[4..], sign: 1),
            _ when token.StartsWith("now-", StringComparison.Ordinal) => NowOffset(token[4..], sign: -1),
            _ when token.StartsWith("request.", StringComparison.Ordinal) && request is not null
                => request.Resolve(token[8..]),
            _ when extraBindings is not null && extraBindings.TryGetValue(token, out var bound) => bound,
            _ => null
        };
    }

    private static string? NowOffset(string numberPart, int sign)
    {
        if (!int.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return null;
        var shifted = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (sign * (long)seconds);
        return shifted.ToString(CultureInfo.InvariantCulture);
    }

    private static uint RandomUInt32()
    {
        // Full uint32 range from a crypto RNG — avoids the non-crypto-random
        // analyzer flag without a suppression (the value isn't a secret).
        Span<byte> b = stackalloc byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b);
    }
}
