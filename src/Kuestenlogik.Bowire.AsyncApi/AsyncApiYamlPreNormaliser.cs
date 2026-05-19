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

    /// <summary>
    /// Quote the value of every <c>asyncapi:</c> and <c>version:</c> key in
    /// <paramref name="yaml"/>. Idempotent: already-quoted values match the
    /// negative-lookahead in the regex and are skipped.
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
    /// </remarks>
    public static string Normalise(string yaml)
    {
        if (string.IsNullOrEmpty(yaml)) return yaml;
        return EnumTypedKeyPattern.Replace(yaml, QuoteValue);
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
