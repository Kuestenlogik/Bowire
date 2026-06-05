// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Disk-backed store for Bowire recordings — named, ordered sequences
/// of captured invocations that can be replayed, exported (HAR / JSON)
/// or converted into test assertions. Resolves its on-disk path through
/// <see cref="BowireUserContext"/> so single-user installs land at
/// <c>~/.bowire/recordings.json</c> (legacy behaviour, unchanged) and
/// multi-tenant installs route to a per-identity slot under
/// <c>~/.bowire-server/users/&lt;sub&gt;/</c> once the SCIM phase ships
/// (#28 Phase C).
/// </summary>
internal static class RecordingStore
{
    private static string? _testStorePathOverride;

    /// <summary>
    /// On-disk store location. Resolves through
    /// <see cref="BowireUserContext.GetUserPath"/> by default so the
    /// per-user-scoping seam (#28) can swap in a multi-tenant
    /// resolver without touching this class. Tests can pin a specific
    /// path via the setter to redirect into a temp directory without
    /// clobbering the developer's real <c>~/.bowire/</c>.
    /// </summary>
    internal static string StorePath
    {
        get => _testStorePathOverride ?? BowireUserContext.GetUserPath("recordings.json");
        set => _testStorePathOverride = value;
    }

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
