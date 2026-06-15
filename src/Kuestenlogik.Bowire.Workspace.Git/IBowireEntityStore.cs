// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Workspace.Git;

/// <summary>
/// Per-entity reader/writer the workbench routes through when a
/// workspace's <c>storageRoot</c> is set (#196 Phase 2.2). Replaces the
/// legacy single-bundle layout — environments, collections, recordings,
/// scripts and flows each live in their own subdirectory under the
/// checked-out workspace folder so PR diffs show one JSON document per
/// edited entity rather than the entire bundle on every keystroke.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="FileEntityStore"/> is the concrete on-disk implementation.
/// Tests + Phase 2.4's FS-watch SSE producer can plug their own
/// implementation in via DI without dragging the file-backed shape in.
/// </para>
/// <para>
/// All methods take an <c>entityKind</c> string rather than an enum so
/// plugins can carry their own per-entity buckets in a future phase
/// without us reshaping the interface. Today the canonical set is
/// <c>"environments" | "collections" | "recordings" | "scripts" | "flows"</c>
/// — implementations validate against that set up-front so a typo in
/// the workbench (e.g. <c>"environment"</c> singular) fails fast with a
/// loud <see cref="ArgumentException"/> rather than silently writing
/// outside the canonical layout.
/// </para>
/// </remarks>
public interface IBowireEntityStore
{
    /// <summary>
    /// Enumerate the ids of every entity of <paramref name="entityKind"/>
    /// currently on disk. Order is implementation-defined; the workbench
    /// sorts client-side for stable display.
    /// </summary>
    Task<IReadOnlyList<string>> ListAsync(string entityKind, CancellationToken ct = default);

    /// <summary>
    /// Read the raw JSON document for <paramref name="id"/> within
    /// <paramref name="entityKind"/>. Returns <c>null</c> when the entity
    /// is absent — callers treat that as "the user hasn't authored one
    /// yet" and don't surface an error.
    /// </summary>
    Task<string?> LoadAsync(string entityKind, string id, CancellationToken ct = default);

    /// <summary>
    /// Persist <paramref name="json"/> as the canonical document for
    /// <paramref name="id"/> within <paramref name="entityKind"/>.
    /// Implementations create the per-kind directory on first write so
    /// the workbench doesn't have to scaffold the layout up-front.
    /// </summary>
    Task SaveAsync(string entityKind, string id, string json, CancellationToken ct = default);

    /// <summary>
    /// Remove the entity (and any per-request side files for collections).
    /// No-op when the entity is already absent so callers can re-run a
    /// delete idempotently.
    /// </summary>
    Task DeleteAsync(string entityKind, string id, CancellationToken ct = default);
}
