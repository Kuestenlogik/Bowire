// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Projects;

/// <summary>
/// Auto-discovery + loading for the <c>.bowire/project.json</c> convention
/// (#172). <see cref="Discover"/> walks UP the directory tree from a start
/// directory (default: the current working directory) looking for
/// <c>.bowire/project.json</c> — the same "find the nearest config walking
/// toward the repo root" shape git, npm, and dotnet use — so a command run
/// anywhere inside a checkout picks up the project without a flag. It never
/// throws on "not found": it returns <see langword="null"/>, and the caller
/// decides whether that's an error. <see cref="Load"/> reads a specific file
/// and hands the text to <see cref="BowireProjectFile.Parse"/> (which throws on
/// malformed / shape-invalid content).
/// </summary>
public static class BowireProjectLoader
{
    /// <summary>The convention directory that holds the manifest.</summary>
    public const string ConventionDirName = ".bowire";

    /// <summary>The convention manifest filename inside <see cref="ConventionDirName"/>.</summary>
    public const string ConventionFileName = "project.json";

    /// <summary>
    /// Walk up from <paramref name="startDirectory"/> (default: the current
    /// working directory) looking for <c>.bowire/project.json</c>. Returns the
    /// first match toward the filesystem root, or <see langword="null"/> when
    /// none exists anywhere up the chain. Never throws for "not found" — an
    /// unreadable / malformed start path also yields <see langword="null"/>.
    /// </summary>
    public static BowireProjectLocation? Discover(string? startDirectory = null)
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
            var candidate = Path.Combine(current.FullName, ConventionDirName, ConventionFileName);
            if (File.Exists(candidate))
                return new BowireProjectLocation(candidate, current.FullName);
        }

        return null;
    }

    /// <summary>
    /// Read and parse the manifest at <paramref name="filePath"/>. Propagates
    /// <see cref="System.Text.Json.JsonException"/> / <see cref="ArgumentException"/>
    /// from <see cref="BowireProjectFile.Parse"/>, and lets an IO failure
    /// surface as its native exception (a caller that passed an explicit path
    /// wants to know the file couldn't be read).
    /// </summary>
    public static BowireProjectFile Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return BowireProjectFile.Parse(File.ReadAllText(filePath));
    }
}

/// <summary>
/// A located manifest: the resolved file path plus the project root — the
/// directory that CONTAINS <c>.bowire/</c>, so the manifest's project-relative
/// paths (schemas, suites, auth flow, rules) resolve against it.
/// </summary>
/// <param name="FilePath">Absolute path to the discovered <c>.bowire/project.json</c>.</param>
/// <param name="ProjectRoot">Absolute path to the directory that owns the <c>.bowire/</c> folder.</param>
public sealed record BowireProjectLocation(string FilePath, string ProjectRoot);
