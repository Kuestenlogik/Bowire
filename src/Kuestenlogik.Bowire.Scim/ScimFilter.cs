// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Scim;

/// <summary>A filter expression this implementation will not evaluate.</summary>
public sealed class ScimFilterException : Exception
{
    /// <summary>An unusable filter, unexplained.</summary>
    public ScimFilterException() { }

    /// <summary>An unusable filter, with the reason a connector should log.</summary>
    public ScimFilterException(string message) : base(message) { }

    /// <summary>An unusable filter, wrapping what actually failed.</summary>
    public ScimFilterException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// The subset of the SCIM filter language (RFC 7644 §3.4.2.2) that identity
/// providers actually send.
/// </summary>
/// <remarks>
/// <para>
/// The full grammar has ten operators, complex attribute paths and value
/// sub-filters. Okta and Entra ID use one shape between them —
/// <c>userName eq "someone@example.com"</c> — with <c>externalId eq</c> and
/// the occasional <c>and</c> where a connector was configured by hand.
/// </para>
/// <para>
/// So this parses <c>eq</c>, <c>pr</c>, <c>and</c> and <c>or</c>, with
/// <c>and</c> binding tighter, and <em>refuses</em> everything else with a
/// 400 and <c>invalidFilter</c>. Refusing is the point: a parser that
/// silently ignores the half of an expression it did not understand answers
/// a different question than the one asked, and the caller has no way to
/// tell. A connector that gets <c>invalidFilter</c> logs something an
/// operator can act on.
/// </para>
/// </remarks>
public sealed class ScimFilter
{
    private readonly Node _root;

    private ScimFilter(Node root) => _root = root;

    /// <summary>
    /// Parse <paramref name="text"/>.
    /// </summary>
    /// <exception cref="ScimFilterException">
    /// The expression uses something outside the supported subset.
    /// </exception>
    public static ScimFilter Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var tokens = Tokenise(text);
        var position = 0;
        var root = ParseOr(tokens, ref position);

        if (position != tokens.Count)
        {
            throw new ScimFilterException(
                $"Unexpected '{tokens[position]}' at the end of the filter. "
                + "Supported: attribute eq \"value\", attribute pr, joined with and / or.");
        }

        return new ScimFilter(root);
    }

    /// <summary>
    /// Whether a resource matches, where <paramref name="attribute"/> resolves
    /// an attribute name to its value (<c>null</c> when absent).
    /// </summary>
    public bool Matches(Func<string, string?> attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return _root.Matches(attribute);
    }

    // ---- parsing ----

    private static Node ParseOr(List<string> tokens, ref int position)
    {
        var left = ParseAnd(tokens, ref position);
        while (position < tokens.Count && Is(tokens[position], "or"))
        {
            position++;
            var right = ParseAnd(tokens, ref position);
            left = new Either(left, right);
        }
        return left;
    }

    private static Node ParseAnd(List<string> tokens, ref int position)
    {
        var left = ParseComparison(tokens, ref position);
        while (position < tokens.Count && Is(tokens[position], "and"))
        {
            position++;
            var right = ParseComparison(tokens, ref position);
            left = new Both(left, right);
        }
        return left;
    }

    private static Node ParseComparison(List<string> tokens, ref int position)
    {
        if (position >= tokens.Count)
        {
            throw new ScimFilterException("The filter ends where an attribute was expected.");
        }

        var attribute = tokens[position++];
        if (attribute.StartsWith('"'))
        {
            throw new ScimFilterException($"Expected an attribute name, got the literal {attribute}.");
        }

        if (position >= tokens.Count)
        {
            throw new ScimFilterException($"'{attribute}' is not followed by an operator.");
        }

        var op = tokens[position++];
        if (Is(op, "pr")) return new Presence(attribute);

        if (!Is(op, "eq"))
        {
            // Deliberately not "treated as eq". co / sw / gt / ge and the rest
            // mean different things, and answering a contains-query with an
            // equality result is worse than saying no.
            throw new ScimFilterException(
                $"Operator '{op}' is not supported. This service provider implements eq and pr.");
        }

        if (position >= tokens.Count)
        {
            throw new ScimFilterException($"'{attribute} eq' is missing its value.");
        }

        return new Equality(attribute, Unquote(tokens[position++]));
    }

    private static List<string> Tokenise(string text)
    {
        var tokens = new List<string>();
        var index = 0;

        while (index < text.Length)
        {
            var ch = text[index];
            if (char.IsWhiteSpace(ch)) { index++; continue; }

            if (ch == '"')
            {
                var start = index++;
                while (index < text.Length && text[index] != '"')
                {
                    // A backslash escapes the next character, so a quoted value
                    // containing one does not end the literal early.
                    if (text[index] == '\\' && index + 1 < text.Length) index++;
                    index++;
                }
                if (index >= text.Length)
                {
                    throw new ScimFilterException("A quoted value in the filter is never closed.");
                }
                tokens.Add(text[start..++index]);
                continue;
            }

            // Parentheses would change grouping, and silently dropping them
            // would change the answer. Refuse rather than reinterpret.
            if (ch is '(' or ')' or '[' or ']')
            {
                throw new ScimFilterException(
                    "Grouped and value-path filters are not supported by this service provider.");
            }

            var word = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index])
                && text[index] is not ('"' or '(' or ')' or '[' or ']'))
            {
                index++;
            }
            tokens.Add(text[word..index]);
        }

        if (tokens.Count == 0) throw new ScimFilterException("The filter is empty.");
        return tokens;
    }

    private static string Unquote(string token)
        => token.Length >= 2 && token[0] == '"' && token[^1] == '"'
            ? token[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal)
            : token;

    private static bool Is(string token, string keyword)
        => string.Equals(token, keyword, StringComparison.OrdinalIgnoreCase);

    // ---- evaluation ----

    private abstract class Node
    {
        public abstract bool Matches(Func<string, string?> attribute);
    }

    private sealed class Equality(string attribute, string value) : Node
    {
        public override bool Matches(Func<string, string?> resolve)
        {
            var actual = resolve(attribute);
            // Case-insensitive: RFC 7643 §2.1 makes string attributes
            // caseExact=false unless the schema says otherwise, and userName —
            // the attribute every connector filters on — is one of them.
            return actual is not null
                && string.Equals(actual, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class Presence(string attribute) : Node
    {
        public override bool Matches(Func<string, string?> resolve)
            => !string.IsNullOrEmpty(resolve(attribute));
    }

    private sealed class Both(Node left, Node right) : Node
    {
        public override bool Matches(Func<string, string?> resolve)
            => left.Matches(resolve) && right.Matches(resolve);
    }

    private sealed class Either(Node left, Node right) : Node
    {
        public override bool Matches(Func<string, string?> resolve)
            => left.Matches(resolve) || right.Matches(resolve);
    }
}
