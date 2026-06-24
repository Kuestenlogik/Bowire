// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// Workaround layer for <see href="https://github.com/asyncapi/net-sdk/issues/76"/>:
/// the Neuroglia SDK's <c>StringEnumDeserializer</c> blows up on unquoted scalars
/// that YAML's implicit-type resolver classifies as numeric (e.g.
/// <c>asyncapi: 3.0.0</c>, <c>info.version: 1.2.3</c>). Real AsyncAPI documents
/// in the wild leave these unquoted because the spec doesn't mandate strings
/// and every other tool accepts them.
///
/// This pre-normaliser walks the raw YAML text and quotes the values of a
/// fixed set of top-level keys that the SDK insists on enum-deserialising. It
/// runs once per <c>LoadAsync</c> call before the SDK reader sees the document,
/// so the rest of the pipeline can stay vanilla.
///
/// Deliberately regex-based instead of YAML-tree-aware: full re-serialisation
/// would re-format the file (collapse anchors, drop comments, reorder maps)
/// and break <c>$ref</c>-resolution against the original on-disk path. The
/// regex touches one scalar per matching line and leaves everything else byte-
/// identical.
///
/// Drop this class once the upstream SDK is patched and the <c>Neuroglia.AsyncApi.*</c>
/// reference moves past the version that ships the fix.
/// </summary>
internal static class AsyncApiYamlPreNormaliser
{
    // Match `key: value` where:
    //   - the line starts with whitespace + the key, then `:`, then more whitespace
    //   - the value's first character is something we want to quote (not
    //     already a quote, not a block-scalar indicator `|`/`>`, not the
    //     `#` of a comment, and not whitespace itself)
    //   - we capture the raw value up to end-of-line / first `#` comment
    //
    // The `(?>\s*)` atomic group on the post-colon whitespace is the load-
    // bearing piece: a plain `\s*` would let the engine backtrack across
    // the space and let a `'` value start with the leading whitespace
    // (turning `asyncapi: '3.0.0'` into a match whose captured value is
    // ` '3.0.0'`, which we'd then re-quote into `' '3.0.0''` — broken).
    // Atomic = no backtracking once the spaces are consumed.
    //
    // Quote the value with single quotes — single quotes are the YAML form
    // that doesn't process escape sequences, so we preserve the literal
    // value even if it contained backslashes.
    private static readonly Regex EnumTypedKeyPattern = new(
        @"^(?<indent>\s*)(?<key>asyncapi|version)\s*:(?>\s*)(?<value>[^'""\|\>#\r\n\s][^#\r\n]*?)(?<trail>\s*(?:#.*)?)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Canonical AsyncAPI binding-key names per asyncapi/bindings repo.
    // Authors write these inside `bindings:` blocks in a handful of
    // places (channel-, operation-, message-, server-level). The spec
    // is strict about casing — `KAFKA` / `Kafka` / `kafka` are not
    // interchangeable at the parser layer — but documents in the wild
    // routinely upper-case the binding key out of habit (e.g. `MQTT`
    // is the marketing capitalisation; `WebSocket` is the .NET
    // convention). Same goes for the alias map: AsyncAPI 2.x docs
    // sometimes write `websocket` where the spec mandates `ws`. We
    // normalise both ahead of the SDK reader.
    //
    // Order matters here only insofar as alias resolution runs after
    // lowercase (so `WebSocket` → `websocket` → `ws`). Both rules
    // are scoped to the line directly under a `bindings:` parent so
    // we don't accidentally rewrite a `Kafka:` server name or a
    // `MQTT:` channel key elsewhere in the document.
    private static readonly HashSet<string> CanonicalBindingKeys = new(StringComparer.Ordinal)
    {
        "amqp", "amqp1", "anypointmq", "googlepubsub", "http", "ibmmq",
        "jms", "kafka", "mercure", "mqtt", "mqtt5", "nats", "pulsar",
        "redis", "sns", "solace", "sqs", "stomp", "ws"
    };

    // Alias → canonical map. Lower-case keys only — alias resolution
    // happens after the lowercase pass. Keep these conservative: each
    // alias here corresponds to a name the spec authors explicitly
    // discuss in commit history or migration notes, not arbitrary
    // synonyms.
    private static readonly Dictionary<string, string> BindingKeyAliases = new(StringComparer.Ordinal)
    {
        ["websocket"] = "ws",
        ["websockets"] = "ws",
        ["amqp091"] = "amqp",
        ["amqp10"] = "amqp1"
    };


    /// <summary>
    /// Quote the value of every <c>asyncapi:</c> and <c>version:</c> key in
    /// <paramref name="yaml"/> and normalise binding-block child keys
    /// (lowercase + alias resolution). Idempotent: already-quoted values
    /// and already-canonical binding keys both pass through unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>version</c> rule deliberately matches every <c>version:</c> key
    /// at any indentation level. AsyncAPI uses it inside <c>info</c>,
    /// <c>components.schemas.*</c>, and <c>tags[]</c>, all of which trip
    /// the same enum-deserialiser path. Over-quoting <c>version:</c> values
    /// that the SDK happens to deserialise as plain strings anyway is a
    /// no-op for the SDK — single-quoted strings are still strings.
    /// </para>
    /// <para>
    /// Binding-key normalisation walks the document line-by-line and
    /// rewrites child keys that sit directly under a <c>bindings:</c>
    /// header. The spec is case-sensitive (<c>kafka</c>, not <c>Kafka</c>)
    /// but documents in the wild routinely upper-case the binding key
    /// out of habit — same for the alias map (<c>websocket</c> →
    /// <c>ws</c>, <c>amqp091</c> → <c>amqp</c>). Rewriting only inside
    /// the bindings block (rather than globally) avoids touching
    /// unrelated <c>$ref</c> targets or channel names that happen to
    /// match a binding-key spelling.
    /// </para>
    /// </remarks>
    public static string Normalise(string yaml)
    {
        if (string.IsNullOrEmpty(yaml)) return yaml;
        var quoted = EnumTypedKeyPattern.Replace(yaml, QuoteValue);
        return NormaliseBindingKeys(quoted);
    }

    /// <summary>
    /// Line-by-line scan that lowercases + alias-maps the immediate
    /// children of every <c>bindings:</c> header. The state machine
    /// holds onto the indent column the <c>bindings:</c> header sits
    /// at; only direct children (strictly greater indent, sibling
    /// level relative to each other) get rewritten. As soon as a line
    /// returns to the bindings-header indent (or less), we exit the
    /// rewrite scope.
    /// </summary>
    private static string NormaliseBindingKeys(string yaml)
    {
        // Cheap pre-flight: documents without a `bindings:` header
        // skip the per-line walk entirely.
        if (yaml.IndexOf("bindings", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return yaml;
        }

        var lines = yaml.Split('\n');
        var changed = false;
        // -1 = not currently inside a bindings block
        var bindingsHeaderIndent = -1;
        // Direct-child indent (the column where each binding-key
        // starts). Captured from the first child line so we can
        // tell siblings (same indent) from nested-deeper content
        // (greater indent — that's binding-field bag, leave alone).
        var childKeyIndent = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            // Skip blank lines and comment-only lines: they don't
            // affect indentation tracking. A blank line inside a
            // bindings block is still a blank line — keep going.
            var trimmedLeading = line.TrimStart();
            if (trimmedLeading.Length == 0 || trimmedLeading.StartsWith('#'))
            {
                continue;
            }

            var indent = line.Length - trimmedLeading.Length;

            // If we're inside a bindings block, decide what to do
            // with the line based on its indent.
            if (bindingsHeaderIndent >= 0)
            {
                if (indent <= bindingsHeaderIndent)
                {
                    // De-dented back out of the bindings block — exit.
                    bindingsHeaderIndent = -1;
                    childKeyIndent = -1;
                    // Fall through to the bindings-header detect for
                    // the current line (the line that ended the block
                    // could itself BE another bindings header).
                }
                else
                {
                    // First child line establishes the direct-child
                    // indent. Subsequent direct-child siblings share
                    // it; deeper-indented lines are binding-field
                    // bag — those stay untouched.
                    if (childKeyIndent < 0) childKeyIndent = indent;

                    if (indent == childKeyIndent && TryRewriteBindingKey(line, out var rewritten))
                    {
                        lines[i] = rewritten;
                        changed = true;
                    }
                    // Keep looking — multi-binding blocks declare
                    // several siblings (kafka + ws + http together).
                    continue;
                }
            }

            // Not in a bindings block. Is this line one?
            if (IsBindingsHeader(trimmedLeading))
            {
                bindingsHeaderIndent = indent;
                childKeyIndent = -1;
            }
        }

        return changed ? string.Join('\n', lines) : yaml;
    }

    private static bool IsBindingsHeader(string trimmedLine)
    {
        // `bindings:` or `bindings:` followed by a trailing flow-mapping
        // (`bindings: {}` — a no-op block we shouldn't rewrite, but
        // still mark to keep the state machine simple). Inline-mapping
        // bindings (`bindings: {kafka: {topic: x}}`) are rare in the
        // wild and not handled here; authors who write those don't
        // hit the casing problem we're fixing.
        if (!trimmedLine.StartsWith("bindings", StringComparison.Ordinal)) return false;
        var rest = trimmedLine[8..].TrimStart();
        return rest.StartsWith(':');
    }

    private static bool TryRewriteBindingKey(string line, out string rewritten)
    {
        // Match `<indent><key><trailing>` where trailing starts with `:`.
        // The key is the literal binding-id (`Kafka`, `WebSocket`, etc.).
        // We don't go through the full regex engine because line-level
        // splits are cheap and the structure here is dead simple.
        var trimmedLeading = line.TrimStart();
        var indent = line[..^trimmedLeading.Length];
        var colon = trimmedLeading.IndexOf(':');
        if (colon <= 0)
        {
            rewritten = line;
            return false;
        }
        var key = trimmedLeading[..colon];
        var trail = trimmedLeading[colon..];

        // Skip keys that contain anything besides letters / digits /
        // hyphens / underscores — that's not a binding-id, that's an
        // anchor / alias / flow node.
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                rewritten = line;
                return false;
            }
        }

        // ToLowerInvariant is correct here: the AsyncAPI spec mandates
        // lower-case binding-id keys ("kafka", "mqtt", "ws"), so the
        // canonical form is by definition lower-case. CA1308 prefers
        // ToUpperInvariant for ordinal comparison, but that's the wrong
        // direction here — we'd then need to invert back to lower-case
        // for the canonical-set membership check.
#pragma warning disable CA1308
        var lowered = key.ToLowerInvariant();
#pragma warning restore CA1308
        if (BindingKeyAliases.TryGetValue(lowered, out var canonical))
        {
            lowered = canonical;
        }

        if (!CanonicalBindingKeys.Contains(lowered))
        {
            // Unknown binding key — leave it alone. The SDK / extractor
            // will pass it through; if it's a typo the document author
            // gets the error from the downstream consumer.
            rewritten = line;
            return false;
        }

        if (string.Equals(lowered, key, StringComparison.Ordinal))
        {
            rewritten = line;
            return false;
        }

        rewritten = indent + lowered + trail;
        return true;
    }

    private static string QuoteValue(Match m)
    {
        // The character class in the regex already guarantees the value
        // starts with a non-quote, non-whitespace character, so we don't
        // need a second check here. Just escape any embedded single quotes
        // by doubling them (YAML single-quoted string convention; rare in
        // version strings but cheap).
        var value = m.Groups["value"].Value;
        var escaped = value.Replace("'", "''", StringComparison.Ordinal);
        return $"{m.Groups["indent"].Value}{m.Groups["key"].Value}: '{escaped}'{m.Groups["trail"].Value}";
    }
}
