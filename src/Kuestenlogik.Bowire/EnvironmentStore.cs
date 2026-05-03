// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Disk-backed store for Bowire environments and global variables.
/// Persists to <c>~/.bowire/environments.json</c> so configurations survive
/// browser changes, machine restarts, and CLI/standalone usage.
/// </summary>
internal static class EnvironmentStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".bowire", "environments.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lock FileLock = new();

    /// <summary>
    /// Load the raw JSON document. Returns an empty default shape when the file
    /// does not exist or is corrupt — never throws so the UI keeps working.
    /// </summary>
    public static string Load()
    {
        lock (FileLock)
        {
            try
            {
                if (!File.Exists(StorePath))
                    return """{"globals":{},"environments":[],"activeEnvId":""}""";

                var json = File.ReadAllText(StorePath);

                // Validate parseability — if corrupt, return empty so the UI can recover
                using var _ = JsonDocument.Parse(json);
                return json;
            }
            catch
            {
                return """{"globals":{},"environments":[],"activeEnvId":""}""";
            }
        }
    }

    /// <summary>
    /// Persist the supplied JSON document. The document is parsed and re-serialized
    /// with indentation so the file stays human-readable for manual editing.
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
