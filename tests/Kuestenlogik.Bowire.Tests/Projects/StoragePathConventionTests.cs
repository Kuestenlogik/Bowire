// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Tests.Projects;

/// <summary>
/// Keeps storage-path resolution in one place (#616).
/// </summary>
/// <remarks>
/// <para>
/// Before this guard, <c>Path.Combine(GetFolderPath(UserProfile), ".bowire",
/// …)</c> appeared in fourteen files across six assemblies. Every one of them
/// was a place the project opt-in, the machine scope and
/// <c>BOWIRE_INSTANCE</c> silently did not reach: the plugin directory, the
/// proxy CA, the vuln-db cache, the MCP stores. The failure mode is the worst
/// kind — the feature looks configured and the process reads from somewhere
/// else while reporting success.
/// </para>
/// <para>
/// A source scan rather than a reflection check, because what has to be
/// prevented is the <em>writing</em> of a second resolution, and the shape it
/// takes is textual. It is a cheap guard against a change that is easy to make
/// by accident and hard to notice afterwards.
/// </para>
/// </remarks>
public sealed class StoragePathConventionTests
{
    /// <summary>
    /// The one legitimate use: the root the resolver itself is built on.
    /// </summary>
    private static readonly string[] Allowed =
    [
        // DefaultBowireUserStore.UserProfileRoot — what BowireStorageRoot
        // resolves to when no project opts in, and therefore the value the
        // resolver's own default delegate reads.
        Path.Combine("Kuestenlogik.Bowire", "Auth", "IBowireUserStore.cs"),

        // McpPaths translates the MCP tests' narrower HomeDirOverride — which
        // names a *home directory*, not a storage root — into a path. It is
        // the one place that translation happens, which is the point of it
        // being on the list rather than spread across four call sites.
        Path.Combine("Kuestenlogik.Bowire.Mcp", "McpPaths.cs"),

        // The resolver is the one type whose job is to know the platform
        // layout — and whose documentation quotes the pattern it replaced.
        Path.Combine("Kuestenlogik.Bowire", "Projects", "BowirePathResolver.cs"),

        // These two answer a different question. They resolve against the
        // *working directory*, not the user profile: a contract result and a
        // benchmark schedule belong to the checkout being tested, and both
        // take an explicit rootPath that CI passes. Routing them through the
        // Data scope would move them out of the repo for anyone who does not,
        // which is a product decision rather than a cleanup — tracked on #616.
        Path.Combine("Kuestenlogik.Bowire.Benchmarking", "BowireBenchmarkScheduleStore.cs"),
        Path.Combine("Kuestenlogik.Bowire.Contracts", "ContractResultStore.cs"),
    ];

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kuestenlogik.Bowire.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    [Fact]
    public void No_Production_Source_Resolves_The_User_Profile_Itself()
    {
        var repo = RepoRoot();
        Assert.SkipWhen(repo is null, "repository root not found from the test output directory");

        var src = Path.Combine(repo!, "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Path.GetRelativePath(src, file);
            if (Allowed.Any(a => relative.EndsWith(a, StringComparison.OrdinalIgnoreCase))) continue;

            var text = File.ReadAllText(file);
            // Comments mentioning it are fine; a call is not.
            if (Regex.IsMatch(text, @"GetFolderPath\s*\(\s*Environment\.SpecialFolder\.UserProfile"))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "These resolve the user profile directly instead of asking IBowirePathResolver / BowirePaths, "
            + "so the project opt-in, the machine scope and BOWIRE_INSTANCE will not reach them:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void No_Production_Source_Hard_Codes_The_Dot_Bowire_Directory_Name()
    {
        // The other half of the same mistake: even code that got the home
        // directory from somewhere sensible still has to be told the folder
        // is called ".bowire", and that string was written out sixteen times.
        var repo = RepoRoot();
        Assert.SkipWhen(repo is null, "repository root not found from the test output directory");

        var src = Path.Combine(repo!, "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Path.GetRelativePath(src, file);
            if (Allowed.Any(a => relative.EndsWith(a, StringComparison.OrdinalIgnoreCase))) continue;

            var text = File.ReadAllText(file);
            // A Path.Combine whose argument list contains the literal — not a
            // doc comment saying "~/.bowire", which is how it should be
            // described to a reader.
            if (Regex.IsMatch(text, @"Path\.Combine\s*\([^;]*""\.bowire"""))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "These build a path containing a literal \".bowire\" instead of resolving through BowirePaths:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders.Order(StringComparer.Ordinal)));
    }
}
