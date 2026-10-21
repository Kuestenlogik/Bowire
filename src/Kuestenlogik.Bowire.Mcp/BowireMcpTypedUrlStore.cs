// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// Disk-backed log of URLs the user has typed into the workbench (or
/// permitted via the MCP <c>bowire.allowlist.permit</c> tool). Powers
/// the <c>--allow-invoke</c> CLI mode: when set, the MCP allowlist seed
/// pulls every URL from here in addition to the environments file. This
/// widens what an agent can hit to "everywhere the user has actually
/// pointed Bowire" without dropping the allowlist entirely.
///
/// <para>
/// Stored at <c>~/.bowire/typed-urls.json</c> as a flat array:
/// <c>["https://api.example.com/v1", "http://localhost:5000", …]</c>.
/// Deduplicated case-insensitively. The frontend may write to the same
/// file independently — this store treats it as an append-only log and
/// re-reads on every <see cref="LoadAll"/>.
/// </para>
/// </summary>
public static class BowireMcpTypedUrlStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private static readonly object FileLock = new();

    /// <summary>
    /// Test-only override for the home directory — lets the coverage
    /// tests target a temp folder instead of the developer's real
    /// <c>~/.bowire/</c>. Production callers leave it null.
    /// </summary>
    internal static string? HomeDirOverride
    {
        get => McpPaths.HomeDirOverride;
        set => McpPaths.HomeDirOverride = value;
    }

    /// <summary>
    /// Resolve the on-disk path the store reads + writes. Mirrors the
    /// resolver in <see cref="BowireMcpTools"/> so a single test-time
    /// override redirects both at once.
    /// </summary>
    public static string FilePath => McpPaths.Config("typed-urls.json");

    /// <summary>
    /// Load every typed URL from disk. Returns an empty list when the
    /// file is missing or unparseable so callers can treat "no history"
    /// and "corrupt history" identically.
    /// </summary>
    public static IReadOnlyList<string> LoadAll()
    {
        lock (FileLock)
        {
            var path = FilePath;
            if (!File.Exists(path)) return Array.Empty<string>();
            try
            {
                return ReadUrls(path) ?? (IReadOnlyList<string>)Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    /// <summary>
    /// Append a URL to the on-disk log. No-ops when the URL is already
    /// present (case-insensitive). Returns <c>true</c> when the URL was
    /// new and got written, <c>false</c> when it was a duplicate or the
    /// input was invalid.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The file exists but is not a JSON array; it is left untouched.
    /// </exception>
    public static bool Add(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        lock (FileLock)
        {
            var path = FilePath;

            // #880 — not LoadAll: it reads an unreadable file as "no
            // history" (right for seeding), and writing that empty list back
            // with one URL appended erased every URL a truncated or
            // differently shaped file still held. A file Add cannot read is
            // one it must not overwrite.
            List<string> existing = [];
            if (File.Exists(path))
            {
                existing = ReadUrls(path)
                    ?? throw new InvalidDataException($"{path} is not a JSON array of URLs; leaving it as it is.");
            }
            if (existing.Any(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase)))
                return false;
            existing.Add(url);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(existing, JsonOpts));
            return true;
        }
    }

    /// <summary>
    /// The non-blank string entries of the array at <paramref name="path"/>,
    /// or <see langword="null"/> when the file is not a JSON array.
    /// </summary>
    private static List<string>? ReadUrls(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<string>(doc.RootElement.GetArrayLength());
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }
            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
