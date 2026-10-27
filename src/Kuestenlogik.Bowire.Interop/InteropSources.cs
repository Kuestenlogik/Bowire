// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop;

/// <summary>
/// Reads a path on disk into the files of an <see cref="ImportCollectionRequest"/>:
/// one file, or every file of a directory (a Bruno collection) with paths
/// relative to it. Shared by the CLI and the MCP tools.
/// </summary>
public static class InteropSources
{
    // Directories a collection checkout carries that are never part of it.
    private static readonly HashSet<string> s_skipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".idea", ".vs",
    };

    /// <summary>The files at <paramref name="path"/>.</summary>
    /// <exception cref="FileNotFoundException">Nothing exists at <paramref name="path"/>.</exception>
    public static async Task<List<SourceFile>> ReadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var files = new List<SourceFile>();
        if (File.Exists(full))
        {
            files.Add(new SourceFile { Path = Path.GetFileName(full), Content = await File.ReadAllTextAsync(full, ct).ConfigureAwait(false) });
            return files;
        }
        if (!Directory.Exists(full)) throw new FileNotFoundException($"{path} does not exist.", full);

        var pending = new Stack<string>();
        pending.Push(full);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!s_skipDirs.Contains(Path.GetFileName(sub))) pending.Push(sub);
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                files.Add(new SourceFile
                {
                    Path = Path.GetRelativePath(full, file).Replace('\\', '/'),
                    Content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false),
                });
            }
        }
        return files;
    }
}
