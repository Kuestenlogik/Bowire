// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests.Internal;

/// <summary>
/// Coverage for <see cref="SafePath.Combine(string, string)"/> — the
/// cs/path-combine wrapper. The contract:
/// <list type="bullet">
///   <item>Relative segments combine the same way Path.Combine does
///     (modulo full-path normalisation).</item>
///   <item>An absolute right-hand side is rejected — the BCL would
///     silently drop the root, classic /etc/passwd surprise.</item>
///   <item>A ../-escaping right-hand side is rejected after
///     normalisation.</item>
///   <item>Empty/null arguments are rejected up front.</item>
///   <item>Root works with or without a trailing separator.</item>
/// </list>
/// </summary>
public sealed class SafePathTests
{
    private static string TempRoot() =>
        Directory.CreateTempSubdirectory("bowire-safepath-").FullName;

    // ----------------------------------------------------------------
    // Happy path
    // ----------------------------------------------------------------

    [Fact]
    public void Combine_relative_segment_lands_under_root()
    {
        var root = TempRoot();
        try
        {
            var combined = SafePath.Combine(root, "recordings.json");
            Assert.StartsWith(root, combined);
            Assert.EndsWith("recordings.json", combined);
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    [Fact]
    public void Combine_multi_segment_relative_lands_under_root()
    {
        var root = TempRoot();
        try
        {
            var combined = SafePath.Combine(root, Path.Combine("workspaces", "ws_a", "env.json"));
            Assert.StartsWith(root, combined);
            Assert.Contains("workspaces", combined);
            Assert.Contains("ws_a", combined);
            Assert.EndsWith("env.json", combined);
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    [Fact]
    public void Combine_three_arg_overload_chains_validation()
    {
        var root = TempRoot();
        try
        {
            var combined = SafePath.Combine(root, "environments", "staging.json");
            Assert.StartsWith(root, combined);
            Assert.EndsWith(Path.Combine("environments", "staging.json"), combined);
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    [Fact]
    public void Combine_three_arg_overload_rejects_absolute_first_segment()
    {
        var root = TempRoot();
        try
        {
            var bad = OperatingSystem.IsWindows() ? @"C:\Windows\System32" : "/etc";
            Assert.Throws<ArgumentException>(() =>
                SafePath.Combine(root, bad, "staging.json"));
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    // ----------------------------------------------------------------
    // Absolute right-hand side rejection — the cs/path-combine footgun
    // ----------------------------------------------------------------

    [Fact]
    public void Combine_rejects_absolute_unix_relative()
    {
        var root = TempRoot();
        try
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                SafePath.Combine(root, "/etc/passwd"));
            Assert.Equal("relative", ex.ParamName);
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    [Fact]
    public void Combine_rejects_absolute_windows_relative()
    {
        // The Windows-rooted form is recognised as rooted on
        // every platform Path.IsPathRooted runs on (the API
        // checks for both unix `/` and DOS `C:\` shapes on
        // Windows, only unix `/` on POSIX). On non-Windows
        // the C:\ form parses as a relative path so the
        // assertion only fires on Windows; gate accordingly.
        if (!OperatingSystem.IsWindows()) return;
        var root = TempRoot();
        try
        {
            Assert.Throws<ArgumentException>(() =>
                SafePath.Combine(root, @"C:\Windows\System32\config\SAM"));
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    // ----------------------------------------------------------------
    // ../-escape rejection (post-normalisation)
    // ----------------------------------------------------------------

    [Fact]
    public void Combine_rejects_dotdot_traversal()
    {
        var root = TempRoot();
        try
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                SafePath.Combine(root, Path.Combine("..", "..", "etc", "passwd")));
            Assert.Equal("relative", ex.ParamName);
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    [Fact]
    public void Combine_allows_inner_dotdot_that_stays_under_root()
    {
        // "a/b/../c.json" normalises to "a/c.json", still under root —
        // the post-normalisation check is what makes this OK; a naive
        // string match would have rejected it.
        var root = TempRoot();
        try
        {
            var combined = SafePath.Combine(root, Path.Combine("a", "b", "..", "c.json"));
            Assert.StartsWith(root, combined);
            Assert.EndsWith(Path.Combine("a", "c.json"), combined);
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    // ----------------------------------------------------------------
    // Empty / null rejection
    // ----------------------------------------------------------------

    [Fact]
    public void Combine_rejects_null_root()
    {
        Assert.Throws<ArgumentNullException>(() => SafePath.Combine(null!, "x.json"));
    }

    [Fact]
    public void Combine_rejects_empty_root()
    {
        Assert.Throws<ArgumentException>(() => SafePath.Combine("", "x.json"));
    }

    [Fact]
    public void Combine_rejects_null_relative()
    {
        var root = TempRoot();
        try
        {
            Assert.Throws<ArgumentNullException>(() =>
                SafePath.Combine(root, null!));
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    [Fact]
    public void Combine_rejects_empty_relative()
    {
        var root = TempRoot();
        try
        {
            Assert.Throws<ArgumentException>(() =>
                SafePath.Combine(root, ""));
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    // ----------------------------------------------------------------
    // Root with / without trailing separator
    // ----------------------------------------------------------------

    [Fact]
    public void Combine_works_with_trailing_separator_on_root()
    {
        var root = TempRoot();
        try
        {
            var withSep = SafePath.Combine(root + Path.DirectorySeparatorChar, "x.json");
            var withoutSep = SafePath.Combine(root, "x.json");
            Assert.Equal(withoutSep, withSep);
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    // ----------------------------------------------------------------
    // Containment edge case: a sibling directory that shares the root's
    // string prefix must NOT pass. e.g. root="/tmp/bowire-foo" and
    // a normalised result of "/tmp/bowire-foo-evil" would naively
    // StartsWith but isn't actually under the root.
    // ----------------------------------------------------------------

    [Fact]
    public void Combine_rejects_sibling_with_shared_prefix()
    {
        // We can't directly force a sibling via the public API (the
        // helper would need a ../-escape that lands sideways), so the
        // assertion lives on the implementation's TrimEnd-plus-sep
        // guard: a normalised path equal to "<root>-foo" must not
        // be accepted as inside root. The check fires when the inner
        // ../ resolves to a sibling directory. Construct that by
        // adding ".." up to the parent then descending into a path
        // that shares the leading name.
        var parent = Directory.CreateTempSubdirectory("bowire-sp-").FullName;
        var root = Path.Combine(parent, "bowire");
        Directory.CreateDirectory(root);
        try
        {
            // ../bowire-evil/x.json from inside <parent>/bowire/
            // normalises to <parent>/bowire-evil/x.json — a sibling
            // that shares the "bowire" prefix. SafePath must reject.
            Assert.Throws<ArgumentException>(() =>
                SafePath.Combine(root, Path.Combine("..", "bowire-evil", "x.json")));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
