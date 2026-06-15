// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Workspace.Git;

/// <summary>
/// File-backed <see cref="IBowireEntityStore"/> implementation — one
/// <c>.json</c> per entity under <c>&lt;storageRoot&gt;/&lt;entityKind&gt;/</c>.
///
/// <para>
/// Collections get an additional per-request layout: each entry that
/// looks like an HTTP request (heuristic: <c>requests</c> array on the
/// document) writes a separate <c>&lt;request-id&gt;.req.json</c> sibling
/// under <c>collections/&lt;collection-id&gt;/</c> so OpenAPI-shaped
/// collection diffs show one request per file in PR review — instead of
/// the whole collection's request array exploding on every edit.
/// </para>
///
/// <para>
/// JSON serialisation matches the workbench's bundle store:
/// <see cref="JsonSerializerOptions.WriteIndented"/> on, camelCase
/// preserved (we don't reshape the document — we trust the caller's
/// shape — but the pretty-print pass keeps diffs reviewable).
/// </para>
///
/// <para>
/// The store is robust against missing directories: every write creates
/// the per-kind directory on demand, every read against a missing file
/// returns <c>null</c>, every delete against a missing target is a
/// no-op. That lets the workbench's "save environment" path land
/// without a separate "create the folder skeleton first" round-trip.
/// </para>
/// </summary>
public sealed class FileEntityStore : IBowireEntityStore
{
    /// <summary>
    /// The canonical per-entity buckets the workbench routes through.
    /// Locked down here so a typo in the workbench (e.g. <c>"environment"</c>
    /// singular) fails fast with an <see cref="ArgumentException"/>
    /// rather than silently writing to a fresh folder outside the
    /// committed layout.
    /// </summary>
    internal static readonly IReadOnlySet<string> CanonicalEntityKinds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "environments",
            "collections",
            "recordings",
            "scripts",
            "flows",
        };

    private static readonly JsonSerializerOptions IndentedJsonOpts = new()
    {
        WriteIndented = true,
    };

    private readonly string _storageRoot;

    /// <summary>
    /// Construct a store rooted at <paramref name="storageRoot"/>.
    /// The path is expected to point at a workspace directory laid out
    /// by <c>bowire workspace init</c> (or migrated via
    /// <see cref="BowireGitWorkspaceMigrator"/>). The directory is created
    /// lazily on first write — passing a path that doesn't exist yet is
    /// fine.
    /// </summary>
    public FileEntityStore(string storageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        _storageRoot = Path.GetFullPath(storageRoot);
    }

    /// <summary>
    /// The on-disk root this store reads + writes through. Exposed so
    /// the migrator can target the same path the workbench will read
    /// from, without re-deriving it from configuration.
    /// </summary>
    public string StorageRoot => _storageRoot;

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(string entityKind, CancellationToken ct = default)
    {
        ValidateEntityKind(entityKind);
        var dir = Path.Combine(_storageRoot, entityKind);
        if (!Directory.Exists(dir))
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        // For collections, ids are either:
        //   - a top-level <id>.json file (collection-only, no requests), OR
        //   - a <id>/ subdirectory containing <id>.json + N <reqId>.req.json
        // For every other kind, ids are <id>.json directly under the bucket.
        var ids = new List<string>();
        if (string.Equals(entityKind, "collections", StringComparison.Ordinal))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                // .req.json siblings live inside a subdir, never at the top
                // level, so any .json here is a single-file collection.
                ids.Add(Path.GetFileNameWithoutExtension(file));
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                ids.Add(Path.GetFileName(sub));
            }
        }
        else
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                ids.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        // Deduplicate (a collection-and-requests pair can show up as both
        // a <id>/<id>.json AND a folder named <id>) and stabilise order.
        return Task.FromResult<IReadOnlyList<string>>(
            ids.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList());
    }

    /// <inheritdoc />
    public async Task<string?> LoadAsync(string entityKind, string id, CancellationToken ct = default)
    {
        ValidateEntityKind(entityKind);
        ValidateEntityId(id);

        var path = ResolveEntityPath(entityKind, id);
        if (!File.Exists(path))
        {
            return null;
        }
        return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveAsync(string entityKind, string id, string json, CancellationToken ct = default)
    {
        ValidateEntityKind(entityKind);
        ValidateEntityId(id);
        ArgumentNullException.ThrowIfNull(json);

        // Re-serialise through JsonDocument so the on-disk format always
        // matches the bundle store's indented camelCase shape, regardless
        // of how minified the caller's input was. Rejects garbage early
        // (JsonException) instead of silently writing it.
        using var doc = JsonDocument.Parse(json);
        var pretty = JsonSerializer.Serialize(doc.RootElement, IndentedJsonOpts);

        var path = ResolveEntityPath(entityKind, id);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        // Collections: write the canonical document AND fan out each
        // request into its own .req.json sibling so PR review sees one
        // request per file. The container <id>.json keeps the request
        // ids in order + carries collection-level metadata (name,
        // description, &c).
        if (string.Equals(entityKind, "collections", StringComparison.Ordinal))
        {
            await SaveCollectionAsync(id, doc, pretty, ct).ConfigureAwait(false);
            return;
        }

        await File.WriteAllTextAsync(path, pretty + Environment.NewLine, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string entityKind, string id, CancellationToken ct = default)
    {
        ValidateEntityKind(entityKind);
        ValidateEntityId(id);

        if (string.Equals(entityKind, "collections", StringComparison.Ordinal))
        {
            // Two shapes to clean up: the per-collection subdirectory
            // (with the container + every .req.json sibling) AND, for
            // the no-requests case, a top-level <id>.json at the bucket
            // root.
            var subDir = Path.Combine(_storageRoot, entityKind, id);
            if (Directory.Exists(subDir))
            {
                Directory.Delete(subDir, recursive: true);
            }
            var topLevel = Path.Combine(_storageRoot, entityKind, id + ".json");
            if (File.Exists(topLevel))
            {
                File.Delete(topLevel);
            }
            return Task.CompletedTask;
        }

        var path = ResolveEntityPath(entityKind, id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    // ----------------------------------------------------------------
    // Collection layout — per-collection subdirectory with the
    // container <id>.json + one .req.json per request entry.
    // ----------------------------------------------------------------

    private async Task SaveCollectionAsync(
        string collectionId, JsonDocument doc, string canonicalPretty, CancellationToken ct)
    {
        var collectionDir = Path.Combine(_storageRoot, "collections", collectionId);
        Directory.CreateDirectory(collectionDir);

        // The container always lands as <id>/<id>.json so the workbench
        // can read collection metadata + the request order without
        // walking the subdir. Trailing newline matches the bundle store.
        var containerPath = Path.Combine(collectionDir, collectionId + ".json");
        await File.WriteAllTextAsync(containerPath, canonicalPretty + Environment.NewLine, ct)
            .ConfigureAwait(false);

        // Wipe any stale .req.json files first — a delete-then-rewrite
        // pass keeps the on-disk view in sync when requests are removed
        // from the collection.
        foreach (var stale in Directory.EnumerateFiles(collectionDir, "*.req.json", SearchOption.TopDirectoryOnly))
        {
            File.Delete(stale);
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("requests", out var requests)
            && requests.ValueKind == JsonValueKind.Array)
        {
            foreach (var req in requests.EnumerateArray())
            {
                if (req.ValueKind != JsonValueKind.Object) continue;
                if (!req.TryGetProperty("id", out var reqId)) continue;
                if (reqId.ValueKind != JsonValueKind.String) continue;
                var reqIdStr = reqId.GetString();
                if (string.IsNullOrWhiteSpace(reqIdStr)) continue;

                var reqPath = Path.Combine(collectionDir, reqIdStr + ".req.json");
                var reqJson = JsonSerializer.Serialize(req, IndentedJsonOpts);
                await File.WriteAllTextAsync(reqPath, reqJson + Environment.NewLine, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    // ----------------------------------------------------------------
    // Path resolution + validation
    // ----------------------------------------------------------------

    private string ResolveEntityPath(string entityKind, string id)
    {
        if (string.Equals(entityKind, "collections", StringComparison.Ordinal))
        {
            // Prefer the per-collection subdir layout when it exists;
            // fall back to a top-level <id>.json so legacy single-file
            // collections (no requests, just metadata) still round-trip.
            var subDir = Path.Combine(_storageRoot, entityKind, id);
            var containerInSubDir = Path.Combine(subDir, id + ".json");
            if (Directory.Exists(subDir))
            {
                return containerInSubDir;
            }
            var topLevel = Path.Combine(_storageRoot, entityKind, id + ".json");
            if (File.Exists(topLevel))
            {
                return topLevel;
            }
            // Default for fresh writes: the subdir shape. Save() decides
            // whether to fan out requests; the container always lives at
            // <id>/<id>.json so the workbench can treat collections as
            // first-class folders.
            return containerInSubDir;
        }

        return Path.Combine(_storageRoot, entityKind, id + ".json");
    }

    internal static void ValidateEntityKind(string entityKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKind);
        if (!CanonicalEntityKinds.Contains(entityKind))
        {
            throw new ArgumentException(
                $"Unknown entity kind '{entityKind}'. Expected one of: {string.Join(", ", CanonicalEntityKinds)}.",
                nameof(entityKind));
        }
    }

    internal static void ValidateEntityId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        // Path-traversal guard — ids land directly in path segments, so
        // anything that could escape the bucket has to be rejected here.
        // Workbench-generated ids are always [a-zA-Z0-9_-]+ so this
        // strict check matches reality.
        foreach (var ch in id)
        {
            if (ch == '/' || ch == '\\' || ch == ':' || ch == '\0')
            {
                throw new ArgumentException(
                    $"Entity id '{id}' contains a path separator. Workbench ids are restricted to safe characters.",
                    nameof(id));
            }
        }
        if (id is "." or "..")
        {
            throw new ArgumentException(
                $"Entity id '{id}' is reserved.", nameof(id));
        }
    }
}
