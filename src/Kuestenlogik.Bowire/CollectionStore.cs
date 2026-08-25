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

    /// <summary>
    /// Where this workspace's collections live. Mirrors
    /// <c>ChunkedRecordingStore.ResolveRootPath</c>: a workspace id (or an
    /// explicit storage root) anchors the file under that workspace, and
    /// only a caller that names neither falls back to the legacy
    /// single-file layout.
    /// </summary>
    /// <remarks>
    /// #612 — collections were the odd one out. Recordings, presets, mock
    /// configs and schema changes were already per workspace on disk while
    /// collections and environments stayed global, so every workspace read
    /// and wrote the same file. The visible symptom was a template's starter
    /// collection vanishing, but the cause was that two workspaces were
    /// never separated here at all.
    /// </remarks>
    internal static string ResolvePath(string? workspaceId, string? storageRoot = null)
    {
        if (_testStorePathOverride is not null
            && string.IsNullOrWhiteSpace(workspaceId)
            && string.IsNullOrWhiteSpace(storageRoot))
        {
            return _testStorePathOverride;
        }

        if (string.IsNullOrWhiteSpace(workspaceId) && string.IsNullOrWhiteSpace(storageRoot))
            return StorePath;

        return BowireUserContext.GetWorkspacePath(
            workspaceId: workspaceId ?? string.Empty,
            storageRoot: storageRoot,
            relativePath: "collections.json");
    }


    private static readonly Lock FileLock = new();

    private const string EmptyEnvelope = """{"collections":[]}""";

    /// <summary>
    /// Load the raw JSON document. Returns an empty
    /// <c>{"collections":[]}</c> shape when the file does not exist
    /// or is corrupt — never throws so the UI keeps working.
    /// </summary>
    public static string Load() => Load(null, null);

    /// <summary>
    /// Load this workspace's collections.
    /// </summary>
    /// <remarks>
    /// A workspace that has never saved returns the empty envelope. It does
    /// NOT inherit the legacy global file: handing those collections to the
    /// first workspace that happens to look is the cross-workspace bleed
    /// this issue exists to end, and it would land on top of whatever a
    /// template just seeded. Existing <c>~/.bowire/collections.json</c> data
    /// stays where it is and is still served to legacy-layout callers.
    /// </remarks>
    public static string Load(string? workspaceId, string? storageRoot = null)
    {
        var path = ResolvePath(workspaceId, storageRoot);

        lock (FileLock)
        {
            try
            {
                // A workspace with no file yet returns empty, and that is a
                // distinct state from "empty on purpose". The client knows
                // which of the two it is looking at — it has its own copy —
                // so the decision belongs there, not here.
                if (!File.Exists(path))
                    return EmptyEnvelope;

                var json = File.ReadAllText(path);
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
    public static void Save(string json) => Save(json, null, null);

    /// <summary>
    /// Persist this workspace's collections. See
    /// <see cref="ResolvePath(string?, string?)"/> for where they land.
    /// </summary>
    public static void Save(string json, string? workspaceId, string? storageRoot = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("JSON payload required", nameof(json));

        // Validate before writing — caller's bug shouldn't poison
        // the on-disk file.
        using var _ = JsonDocument.Parse(json);

        var path = ResolvePath(workspaceId, storageRoot);

        lock (FileLock)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }
    }

}
