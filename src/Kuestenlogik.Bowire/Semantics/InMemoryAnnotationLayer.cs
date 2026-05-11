// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace Kuestenlogik.Bowire.Semantics;

/// <summary>
/// Fast hash-keyed in-memory annotation layer — the session-scope
/// storage for manual edits that have not been persisted to disk yet.
/// </summary>
/// <remarks>
/// <para>
/// Acts as a single layer in the <see cref="LayeredAnnotationStore"/>
/// composition: the store decides which source-priority the layer
/// represents, the layer itself just carries entries keyed by
/// <see cref="AnnotationKey"/>. The default
/// <see cref="LayeredAnnotationStore"/> wiring uses an
/// <see cref="InMemoryAnnotationLayer"/> at
/// <see cref="AnnotationSource.User"/> priority for "manual session
/// edits that survive only until tab close."
/// </para>
/// <para>
/// Backed by <see cref="ConcurrentDictionary{TKey, TValue}"/> for
/// thread-safe reads + writes; concurrent UI mutation and SSE-driven
/// reads can interleave without locking.
/// </para>
/// </remarks>
public sealed class InMemoryAnnotationLayer
{
    private readonly ConcurrentDictionary<AnnotationKey, SemanticTag> _entries = new();

    /// <summary>Number of entries currently held by the layer.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Insert or replace the tag at <paramref name="key"/>. Returns
    /// the layer for chaining.
    /// </summary>
    public InMemoryAnnotationLayer Set(AnnotationKey key, SemanticTag tag)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(tag);
        _entries[key] = tag;
        return this;
    }

    /// <summary>
    /// Remove the entry at <paramref name="key"/>. Returns <c>true</c>
    /// when an entry was actually removed.
    /// </summary>
    public bool Remove(AnnotationKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _entries.TryRemove(key, out _);
    }

    /// <summary>Clear every entry from the layer.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>
    /// Get the raw tag at <paramref name="key"/> without applying any
    /// resolution priority — single-layer lookup.
    /// </summary>
    public SemanticTag? Get(AnnotationKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _entries.TryGetValue(key, out var tag) ? tag : null;
    }

    /// <summary>
    /// Snapshot of every <c>(key, tag)</c> pair currently held. The
    /// returned sequence is detached from the live dictionary, so
    /// concurrent mutation does not throw mid-enumeration.
    /// </summary>
    public IReadOnlyCollection<KeyValuePair<AnnotationKey, SemanticTag>> Snapshot()
        => [.. _entries];
}
