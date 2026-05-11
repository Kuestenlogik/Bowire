// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics;

/// <summary>
/// Read surface of the layered annotation store. Implementations apply
/// the resolution priority (<c>User &gt; Plugin &gt; Auto</c>) internally
/// so callers get the effective answer with one call.
/// </summary>
/// <remarks>
/// <para>
/// The store is intentionally read-mostly. Writes happen through the
/// concrete layers (<see cref="InMemoryAnnotationLayer"/> for session
/// edits, <see cref="JsonFileAnnotationLayer"/> for user / project
/// persistence) — <see cref="LayeredAnnotationStore"/> exposes those
/// layers but does not present a write surface on the read interface,
/// since "set" without a source dimension is ambiguous.
/// </para>
/// </remarks>
public interface IAnnotationStore
{
    /// <summary>
    /// Resolve the effective semantic tag for <paramref name="key"/>,
    /// applying the priority rule: the highest-source non-empty
    /// annotation wins. Returns <c>null</c> when no source claims the
    /// key at all.
    /// </summary>
    /// <remarks>
    /// A user-supplied <see cref="BuiltInSemanticTags.None"/> is a real
    /// value, not the absence of one — it returns
    /// <see cref="BuiltInSemanticTags.None"/>, suppressing any
    /// lower-priority proposal. Only the genuine "no source claims
    /// this key" case returns <c>null</c>.
    /// </remarks>
    SemanticTag? GetEffective(AnnotationKey key);

    /// <summary>
    /// Enumerate every effective annotation across the configured
    /// layers. One entry per <see cref="AnnotationKey"/>, each carrying
    /// the resolved <see cref="SemanticTag"/> and the
    /// <see cref="AnnotationSource"/> that supplied it.
    /// </summary>
    IEnumerable<Annotation> EnumerateEffective();
}
