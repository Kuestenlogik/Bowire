// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Disk-backed store for Bowire collections — Postman-style named
/// groups of saved requests that are run sequentially against the
/// active environment. Pairs with the existing Recordings (auto-
/// captured sessions) and Flows (visual sequence builder); together
/// they cover the three shapes of repeatable invocations.
/// </summary>
/// <remarks>
/// Path resolution flows through <see cref="BowireUserContext"/> so
/// single-user installs land at <c>~/.bowire/collections.json</c>
/// (legacy layout, unchanged) and multi-tenant installs (issue #28
/// Phase C) route to a per-identity slot. Mirrors the
/// <see cref="EnvironmentStore"/> / <see cref="RecordingStore"/>
/// shape: settable test override, file lock, defensive-fallback
/// reads, full-document overwrite on save.
/// </remarks>
internal static class CollectionStore
{
    private static string? _testStorePathOverride;

    /// <summary>
    /// On-disk store location. Resolves through
    /// <see cref="BowireUserContext.GetUserPath"/> so the per-user-
    /// scoping seam (#28) can swap in a multi-tenant resolver without
    /// touching this class. Tests can pin a specific path via the
    /// setter to redirect into a temp directory without clobbering
    /// the developer's real <c>~/.bowire/</c>.
    /// </summary>
    internal static string StorePath
    {
        get => _testStorePathOverride ?? BowireUserContext.GetUserPath("collections.json");
        set => _testStorePathOverride = value;
    }

    private static readonly Lock FileLock = new();

    private const string EmptyEnvelope = """{"collections":[]}""";

    /// <summary>
    /// Load the raw JSON document. Returns an empty
    /// <c>{"collections":[]}</c> shape when the file does not exist
    /// or is corrupt — never throws so the UI keeps working.
    /// </summary>
    public static string Load()
    {
        lock (FileLock)
        {
            try
            {
                if (!File.Exists(StorePath))
                    return EmptyEnvelope;

                var json = File.ReadAllText(StorePath);
                // Validate parseability — if corrupt, return empty so
                // the UI can recover.
                using var _ = JsonDocument.Parse(json);
                return json;
            }
            catch
            {
                return EmptyEnvelope;
            }
        }
    }

    /// <summary>
    /// Persist the JSON document verbatim, creating the parent
    /// directory on the way. Rejects invalid JSON so a corrupt PUT
    /// can't break the on-disk store.
    /// </summary>
    public static void Save(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("JSON payload required", nameof(json));

        // Validate before writing — caller's bug shouldn't poison
        // the on-disk file.
        using var _ = JsonDocument.Parse(json);

        lock (FileLock)
        {
            var dir = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(StorePath, json);
        }
    }
}
