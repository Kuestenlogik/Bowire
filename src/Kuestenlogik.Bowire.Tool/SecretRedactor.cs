// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// #361 — run-scoped secret redactor. Holds the set of resolved secret
/// <em>values</em> (the values of the variables named via <c>--secret</c> /
/// <c>--secret-file</c>) and rewrites every occurrence of each value with a
/// deterministic mask before it reaches a CI output sink (TTY step output,
/// JUnit failure text, SARIF result message, GitHub <c>::error</c>
/// annotation).
/// <para>
/// Pure + immutable: constructed once with the run's secret values, then
/// only queried. The mask is stable across a run so diffs still line up —
/// a value longer than eight characters renders as <c>***</c> plus its last
/// four characters (e.g. <c>***c5f9</c>); a shorter value collapses to a
/// bare <c>***</c>. Empty / whitespace values are dropped so a blank secret
/// never blanks the whole line.
/// </para>
/// </summary>
internal sealed class SecretRedactor
{
    /// <summary>A redactor that holds no secrets and passes text through verbatim.</summary>
    public static SecretRedactor Empty { get; } = new(Array.Empty<string>());

    // Longest value first: a secret that is a substring of another must be
    // masked as part of the longer match, never half-masked on its own.
    // Ties break on ordinal order for deterministic output.
    private readonly (string Value, string Mask)[] _pairs;

    public SecretRedactor(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in values)
        {
            // Never redact "" / whitespace — replacing an empty string blanks
            // everything, and a whitespace secret is not a real secret.
            if (!string.IsNullOrWhiteSpace(v)) distinct.Add(v);
        }

        _pairs = distinct
            .OrderByDescending(v => v.Length)
            .ThenBy(v => v, StringComparer.Ordinal)
            .Select(v => (v, Mask(v)))
            .ToArray();
    }

    /// <summary>True when there are no secrets to mask — <see cref="Redact"/> is a no-op.</summary>
    public bool IsEmpty => _pairs.Length == 0;

    /// <summary>Number of distinct non-empty secret values registered.</summary>
    public int Count => _pairs.Length;

    /// <summary>
    /// Replace every occurrence of every registered secret value in
    /// <paramref name="text"/> with its mask. Null / empty passes through
    /// unchanged; a null return only ever mirrors a null input.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text) || _pairs.Length == 0) return text;
        var result = text;
        foreach (var (value, mask) in _pairs)
        {
            result = result.Replace(value, mask, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>
    /// Deterministic mask for one value: <c>***</c> + the last four
    /// characters when the value is longer than eight characters, otherwise
    /// a bare <c>***</c>. Kept short enough that the tail can't reconstruct
    /// the secret while still letting a human tell two masked values apart.
    /// </summary>
    internal static string Mask(string value)
        => value.Length > 8 ? string.Concat("***", value.AsSpan(value.Length - 4)) : "***";

    /// <summary>
    /// Read a <c>--secret-file</c>: one secret variable name per line, blank
    /// lines and <c>#</c> comments skipped. Mirrors the dotenv-style parsing
    /// the <c>--env-file</c> reader uses, minus the KEY=VALUE split — a
    /// secret file lists names, not values (the value is resolved from the
    /// run's variable scope so it never lives in a checked-in file).
    /// </summary>
    internal static IReadOnlyList<string> ReadNamesFile(string path)
    {
        var names = new List<string>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            names.Add(line);
        }
        return names;
    }

    /// <summary>
    /// Build a redactor from the secret variable <paramref name="names"/> by
    /// resolving each name against <paramref name="env"/> and registering the
    /// non-empty values. Returns <see cref="Empty"/> when nothing resolves,
    /// so a run without secrets pays no redaction cost.
    /// </summary>
    internal static SecretRedactor FromNames(
        IEnumerable<string> names, IReadOnlyDictionary<string, string> env)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(env);
        var values = new List<string>();
        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name) && env.TryGetValue(name.Trim(), out var v))
            {
                values.Add(v);
            }
        }
        return values.Count == 0 ? Empty : new SecretRedactor(values);
    }
}
