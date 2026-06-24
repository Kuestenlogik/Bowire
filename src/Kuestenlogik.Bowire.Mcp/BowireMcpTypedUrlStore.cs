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
    internal static string? HomeDirOverride { get; set; }

    /// <summary>
    /// Resolve the on-disk path the store reads + writes. Mirrors the
    /// resolver in <see cref="BowireMcpTools"/> so a single test-time
    /// override redirects both at once.
    /// </summary>
    public static string FilePath
        => Path.Combine(
            HomeDirOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".bowire", "typed-urls.json");

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
                using var stream = File.OpenRead(path);
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return Array.Empty<string>();

                var list = new List<string>(doc.RootElement.GetArrayLength());
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                        list.Add(s);
                }
                return list;
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
    public static bool Add(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        lock (FileLock)
        {
            var existing = new List<string>(LoadAll());
            if (existing.Any(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase)))
                return false;
            existing.Add(url);

            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(existing, JsonOpts));
            return true;
        }
    }
}
