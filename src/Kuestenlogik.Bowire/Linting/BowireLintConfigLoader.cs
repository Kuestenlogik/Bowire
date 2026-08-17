// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.Linting;

/// <summary>
/// Auto-discovery + loading for the <c>.bowire/rules.json</c> lint config (#189),
/// mirroring <see cref="BowireProjectLoader"/>: <see cref="DiscoverPath"/> walks
/// UP the directory tree from a start directory looking for
/// <c>.bowire/rules.json</c>, so <c>bowire lint</c> run anywhere in a checkout
/// picks up the workspace's rules without a flag. "Not found" is
/// <see langword="null"/>, never an exception. <see cref="Load"/> reads a
/// specific file and hands it to <see cref="BowireLintConfig.Parse"/>.
/// </summary>
public static class BowireLintConfigLoader
{
    /// <summary>The convention filename inside <c>.bowire/</c>.</summary>
    public const string ConventionFileName = "rules.json";

    /// <summary>
    /// Walk up from <paramref name="startDirectory"/> (default: the current
    /// working directory) looking for <c>.bowire/rules.json</c>. Returns the
    /// first match toward the filesystem root, or <see langword="null"/> when
    /// none exists. Never throws for "not found".
    /// </summary>
    public static string? DiscoverPath(string? startDirectory = null)
    {
        DirectoryInfo? dir;
        try
        {
            var start = string.IsNullOrWhiteSpace(startDirectory)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(startDirectory);
            dir = new DirectoryInfo(start);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }

        for (var current = dir; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, BowireProjectLoader.ConventionDirName, ConventionFileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>Read + parse the config at <paramref name="filePath"/>. Propagates IO / JSON failures.</summary>
    public static BowireLintConfig Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return BowireLintConfig.Parse(File.ReadAllText(filePath));
    }
}
