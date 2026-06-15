// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire;

/// <summary>
/// Path-traversal-safe wrapper around <see cref="Path.Combine(string, string)"/>.
/// The BCL <see cref="Path.Combine(string, string)"/> silently drops earlier
/// arguments when a later one is an absolute path (e.g.
/// <c>Path.Combine("/var/bowire", "/etc/passwd") == "/etc/passwd"</c>). When
/// the right-hand side comes from caller input that's a classic
/// path-traversal footgun — flagged by CodeQL <c>cs/path-combine</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Combine(string, string)"/> rejects an absolute
/// <c>relative</c> outright (the contract is "relative segment under
/// <c>root</c>", so an absolute path is a programmer error, not a
/// request to escape the root). It also rejects <c>../</c> escapes by
/// normalising the combined path and verifying it still lives under
/// the (normalised) root.
/// </para>
/// <para>
/// Use this anywhere a path segment came from outside the assembly —
/// HTTP request bodies, query strings, environment variables, on-disk
/// JSON, &amp;c. Compile-time-constant segments (e.g. <c>"recording.json"</c>)
/// can keep using <see cref="Path.Combine(string, string)"/> directly;
/// the rule's there to catch attacker-controlled inputs, not to ban
/// the BCL helper outright.
/// </para>
/// </remarks>
public static class SafePath
{
    /// <summary>
    /// Combine <paramref name="root"/> with the relative segment
    /// <paramref name="relative"/> and verify the result still lives
    /// under the normalised <paramref name="root"/>.
    /// </summary>
    /// <param name="root">
    /// Absolute (or rooted-relative) path the combined result must
    /// stay under. Passed verbatim into <see cref="Path.GetFullPath(string)"/>
    /// for the containment check; callers should normalise it upstream
    /// when they care about a specific layout.
    /// </param>
    /// <param name="relative">
    /// Relative path segment to append. Must not be empty and must not
    /// itself be a rooted path (the BCL's
    /// <see cref="Path.Combine(string, string)"/> would silently drop
    /// <paramref name="root"/> in that case, which is the bug this
    /// helper exists to prevent).
    /// </param>
    /// <returns>
    /// The fully-normalised combined path, guaranteed to be under
    /// <paramref name="root"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="root"/> or <paramref name="relative"/> is null
    /// or empty; <paramref name="relative"/> is a rooted path; or the
    /// combined path escapes <paramref name="root"/> after normalisation
    /// (e.g. via <c>../</c> segments).
    /// </exception>
    public static string Combine(string root, string relative)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(relative);

        // BCL Path.Combine drops earlier args when a later one is
        // rooted — the exact behaviour CodeQL cs/path-combine flags.
        // Reject before the combine so the caller learns the input
        // shape is wrong rather than silently writing to /etc.
        if (Path.IsPathRooted(relative))
        {
            throw new ArgumentException(
                "Expected a relative path; got an absolute path: " + relative,
                nameof(relative));
        }

        var combined = Path.Combine(root, relative);

        // Containment check after normalisation — Path.GetFullPath
        // resolves "../" segments so we can compare against the root.
        var fullCombined = Path.GetFullPath(combined);
        var fullRoot = Path.GetFullPath(root);
        var rootWithSep = fullRoot.TrimEnd(Path.DirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;

        if (!fullCombined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullCombined, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Relative path escapes the root: " + relative,
                nameof(relative));
        }

        return fullCombined;
    }

    /// <summary>
    /// Three-segment variant — chains
    /// <see cref="Combine(string, string)"/> so each pair is
    /// independently validated. Use when the caller has two
    /// attacker-controlled segments to anchor under <paramref name="root"/>.
    /// </summary>
    public static string Combine(string root, string seg1, string seg2)
        => Combine(Combine(root, seg1), seg2);
}
