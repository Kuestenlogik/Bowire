// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Semantics;

/// <summary>
/// File-backed annotation layer. Loads and persists a single
/// <c>bowire.schema-hints.json</c> file (project or user scope — same
/// format) and exposes the parsed entries via the same
/// <see cref="AnnotationKey"/> / <see cref="SemanticTag"/> surface as
/// <see cref="InMemoryAnnotationLayer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reads are cached in-memory after the first <see cref="LoadAsync"/>;
/// subsequent <see cref="Entries"/> reads hit the cache. Writes
/// (<see cref="SaveAsync"/>) go through a write-to-temp-then-rename
/// sequence so a concurrent reader never sees a half-written file —
/// the same atomic-replacement pattern the rest of Bowire's disk-sync
/// layers use.
/// </para>
/// <para>
/// Concurrency: a per-instance <see cref="SemaphoreSlim"/> serialises
/// writes against one another. Two <see cref="JsonFileAnnotationLayer"/>
/// instances writing to the same file path defend against corruption
/// via the atomic rename — the last writer wins the file, intermediate
/// states are never observable on disk.
/// </para>
/// </remarks>
public sealed class JsonFileAnnotationLayer : IDisposable
{
    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _writeLock = new(initialCount: 1, maxCount: 1);
    private readonly ConcurrentDictionary<AnnotationKey, SemanticTag> _cache = new();
    private bool _loaded;

    /// <summary>
    /// Construct a layer pointing at <paramref name="filePath"/>. The
    /// file does not have to exist — a missing file is treated as an
    /// empty layer and is only written on the first
    /// <see cref="SaveAsync"/>.
    /// </summary>
    public JsonFileAnnotationLayer(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FilePath = filePath;
    }

    /// <summary>Absolute or relative path the layer reads from / writes to.</summary>
    public string FilePath { get; }

    /// <summary>True after <see cref="LoadAsync"/> has completed at least once.</summary>
    public bool IsLoaded => _loaded;

    /// <summary>Number of entries currently cached.</summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Load (or reload) the file into the in-memory cache. Idempotent:
    /// reload clears the cache before re-parsing. A missing file
    /// resets the cache to empty and is not an error — that is the
    /// "nothing persisted yet" steady state.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath))
        {
            _cache.Clear();
            _loaded = true;
            return;
        }

        using var stream = new FileStream(
            FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);

        var file = await JsonSerializer.DeserializeAsync<SchemaHintsFile>(
            stream, s_serializerOptions, ct);

        _cache.Clear();
        if (file is not null)
        {
            HydrateCache(file);
        }
        _loaded = true;
    }

    /// <summary>
    /// Persist the current cache to <see cref="FilePath"/>. Writes go
    /// through a <c>FilePath + ".tmp"</c> file that is then atomically
    /// renamed over the destination, so concurrent readers never see a
    /// half-written document. Parent directories are created on
    /// demand.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var file = BuildFileFromCache();

            // .tmp suffix is unique-per-instance so two layers writing
            // concurrently to the same FilePath don't trample one
            // another's temp file before the rename. The atomic
            // File.Move(..., overwrite: true) at the end then decides
            // last-writer-wins — both processes' content remains
            // self-consistent.
            var tmpPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            await using (var stream = new FileStream(
                tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, file, s_serializerOptions, ct);
            }

            await ReplaceWithRetryAsync(tmpPath, FilePath, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Atomic-rename with a short bounded retry loop. Windows
    /// occasionally throws <see cref="UnauthorizedAccessException"/>
    /// when two processes call <see cref="File.Move(string, string, bool)"/>
    /// on the same destination within microseconds of each other —
    /// the previous winner's handle is still being torn down. A few
    /// 10–50 ms retries clear the race in practice; we abandon after
    /// the budget so a genuine permission problem still surfaces.
    /// </summary>
    private static async Task ReplaceWithRetryAsync(string tmpPath, string destPath, CancellationToken ct)
    {
        const int maxAttempts = 16;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmpPath, destPath, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < maxAttempts - 1)
                {
                    await Task.Delay(10 + (attempt * 10), ct);
                }
                catch (UnauthorizedAccessException) when (attempt < maxAttempts - 1)
                {
                    await Task.Delay(10 + (attempt * 10), ct);
                }
            }
        }
        catch
        {
            // Best-effort cleanup of the temp file when the rename
            // ultimately failed — otherwise repeated SaveAsync calls
            // litter the directory with stale .tmp files.
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* swallow */ }
            throw;
        }
    }

    /// <summary>
    /// Replace the cache content with <paramref name="entries"/>. Does
    /// not touch disk; call <see cref="SaveAsync"/> separately to
    /// persist. Use this to push a freshly-edited set of annotations
    /// into the layer before saving.
    /// </summary>
    public void Replace(IEnumerable<KeyValuePair<AnnotationKey, SemanticTag>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _cache.Clear();
        foreach (var (k, v) in entries)
        {
            if (k is null || v is null) continue;
            _cache[k] = v;
        }
        _loaded = true;
    }

    /// <summary>Get the raw tag at <paramref name="key"/> (no priority resolution).</summary>
    public SemanticTag? Get(AnnotationKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _cache.TryGetValue(key, out var tag) ? tag : null;
    }

    /// <summary>Snapshot of every cached entry.</summary>
    public IReadOnlyCollection<KeyValuePair<AnnotationKey, SemanticTag>> Entries => [.. _cache];

    /// <inheritdoc/>
    public void Dispose()
    {
        _writeLock.Dispose();
    }

    private void HydrateCache(SchemaHintsFile file)
    {
        foreach (var entry in file.Schemas)
        {
            if (string.IsNullOrEmpty(entry.Service) ||
                string.IsNullOrEmpty(entry.Method)) continue;

            foreach (var (typeKey, paths) in entry.Types)
            {
                var msgType = string.IsNullOrEmpty(typeKey)
                    ? AnnotationKey.Wildcard
                    : typeKey;

                foreach (var (jsonPath, kind) in paths)
                {
                    if (string.IsNullOrEmpty(jsonPath) ||
                        string.IsNullOrEmpty(kind)) continue;

                    var key = new AnnotationKey(entry.Service, entry.Method, msgType, jsonPath);
                    _cache[key] = new SemanticTag(kind);
                }
            }
        }
    }

    private SchemaHintsFile BuildFileFromCache()
    {
        // Group cached entries back into the on-disk shape:
        //   service → method → (discriminator, type-value → json-path → kind)
        var grouped = new SortedDictionary<(string Service, string Method),
            SortedDictionary<string, SortedDictionary<string, string>>>(
            Comparer<(string, string)>.Default);

        foreach (var (key, tag) in _cache)
        {
            var sm = (key.ServiceId, key.MethodId);
            if (!grouped.TryGetValue(sm, out var byType))
            {
                byType = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
                grouped[sm] = byType;
            }

            if (!byType.TryGetValue(key.MessageType, out var paths))
            {
                paths = new SortedDictionary<string, string>(StringComparer.Ordinal);
                byType[key.MessageType] = paths;
            }

            paths[key.JsonPath] = tag.Kind;
        }

        var file = new SchemaHintsFile { Version = SchemaHintsFile.CurrentVersion };
        foreach (var ((service, method), byType) in grouped)
        {
            var entry = new SchemaHintsEntry
            {
                Service = service,
                Method = method,
            };

            foreach (var (typeValue, paths) in byType)
            {
                var pathDict = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (jp, kind) in paths) pathDict[jp] = kind;
                entry.Types[typeValue] = pathDict;
            }

            file.Schemas.Add(entry);
        }

        return file;
    }
}
