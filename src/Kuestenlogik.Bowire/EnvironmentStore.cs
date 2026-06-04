// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Disk-backed store for Bowire environments and global variables.
/// Resolves its on-disk path through <see cref="BowireUserContext"/> so
/// single-user installs land at <c>~/.bowire/environments.json</c>
/// (legacy behaviour, unchanged) and multi-tenant installs route to a
/// per-identity slot under <c>~/.bowire-server/users/&lt;sub&gt;/</c>
/// once the SCIM phase ships (#28 Phase C).
/// </summary>
internal static class EnvironmentStore
{
    private static string? _testStorePathOverride;

    /// <summary>
    /// On-disk store location. Resolves through
    /// <see cref="BowireUserContext.GetUserPath"/> by default so the
    /// per-user-scoping seam (#28) can swap in a multi-tenant resolver
    /// without touching this class. Tests can pin a specific path via
    /// the setter to redirect into a temp directory without clobbering
    /// the developer's real <c>~/.bowire/</c>.
    /// </summary>
    internal static string StorePath
    {
        get => _testStorePathOverride ?? BowireUserContext.GetUserPath("environments.json");
        set => _testStorePathOverride = value;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lock FileLock = new();

    private const string EmptyEnvelope = """{"globals":{},"environments":[],"activeEnvId":""}""";

    /// <summary>
    /// Load the JSON document normalised to the current envelope shape
    /// (<c>globals</c> + <c>environments</c> + <c>activeEnvId</c>). Returns the
    /// empty default when the file does not exist or is corrupt — never throws
    /// so the UI keeps working. Pre-globals files on disk (envelope missing
    /// <c>globals</c> or <c>activeEnvId</c>) are extended in-memory so every
    /// consumer sees the same shape regardless of upgrade history.
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
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return EmptyEnvelope;

                var hasGlobals = doc.RootElement.TryGetProperty("globals", out _);
                var hasEnvs = doc.RootElement.TryGetProperty("environments", out _);
                var hasActive = doc.RootElement.TryGetProperty("activeEnvId", out _);
                if (hasGlobals && hasEnvs && hasActive)
                    return json;

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    if (!hasGlobals)
                    {
                        writer.WriteStartObject("globals");
                        writer.WriteEndObject();
                    }
                    if (!hasEnvs)
                    {
                        writer.WriteStartArray("environments");
                        writer.WriteEndArray();
                    }
                    if (!hasActive)
                        writer.WriteString("activeEnvId", "");
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        prop.WriteTo(writer);
                    writer.WriteEndObject();
                }
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch
            {
                return EmptyEnvelope;
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
