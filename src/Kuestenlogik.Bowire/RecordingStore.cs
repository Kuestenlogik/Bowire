// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Disk-backed store for Bowire recordings — named, ordered sequences
/// of captured invocations that can be replayed, exported (HAR / JSON)
/// or converted into test assertions. Persists to
/// <c>~/.bowire/recordings.json</c> so saved scenarios survive browser
/// changes, machine restarts and CLI/standalone usage. Mirrors the
/// disk-store pattern used by <see cref="EnvironmentStore"/> — same
/// JSON-document validation, same lock semantics, same defensive
/// fall-backs so the UI keeps working when the file is missing or
/// corrupt.
/// </summary>
internal static class RecordingStore
{
    /// <summary>
    /// On-disk store location. Settable so tests can redirect into a temp
    /// directory without clobbering the developer's real <c>~/.bowire/</c>.
    /// Production callers leave it at the default.
    /// </summary>
    internal static string StorePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".bowire", "recordings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lock FileLock = new();

    /// <summary>
    /// Load the raw JSON document. Returns an empty <c>{"recordings":[]}</c>
    /// shape when the file does not exist or is corrupt — never throws so
    /// the UI keeps working.
    /// </summary>
    public static string Load()
    {
        lock (FileLock)
        {
            try
            {
                if (!File.Exists(StorePath))
                    return """{"recordings":[]}""";

                var json = File.ReadAllText(StorePath);

                // Validate parseability — if corrupt, return empty so the UI can recover
                using var _ = JsonDocument.Parse(json);
                return json;
            }
            catch
            {
                return """{"recordings":[]}""";
            }
        }
    }

    /// <summary>
    /// Persist the supplied JSON document. The document is parsed and
    /// re-serialized with indentation so the file stays human-readable
    /// for manual editing. Throws <see cref="JsonException"/> on invalid
    /// JSON — the endpoint surface translates that into a 400 response.
    /// </summary>
    public static void Save(string json)
    {
        lock (FileLock)
        {
            // Validate first — refuse to overwrite with garbage
            using var doc = JsonDocument.Parse(json);
            var pretty = JsonSerializer.Serialize(doc.RootElement, JsonOptions);

            var dir = Path.GetDirectoryName(StorePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(StorePath, pretty);
        }
    }
}
