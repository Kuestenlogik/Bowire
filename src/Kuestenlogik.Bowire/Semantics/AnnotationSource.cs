// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics;

/// <summary>
/// Origin of an annotation. Drives the resolution priority in
/// <see cref="IAnnotationStore.GetEffective"/>: when two sources
/// disagree on the same <see cref="AnnotationKey"/>, the higher-
/// authority source wins.
/// </summary>
/// <remarks>
/// <para>
/// Priority is fixed by the ADR: <c>User &gt; Plugin &gt; Auto &gt; None</c>.
/// The user is ground truth — a user-supplied
/// <see cref="BuiltInSemanticTags.None"/> suppresses everything below.
/// </para>
/// </remarks>
public enum AnnotationSource
{
    /// <summary>
    /// Placeholder for "no annotation found" — only returned by
    /// resolver helpers that want to encode the empty case in-band.
    /// </summary>
    None = 0,

    /// <summary>
    /// Auto-detector proposal (e.g. a built-in WGS84 coordinate
    /// detector). Lowest priority of the three real sources;
    /// overridden by both plugin and user annotations.
    /// </summary>
    Auto = 1,

    /// <summary>
    /// Plugin-supplied schema hint, emitted as part of the
    /// Discovery-Descriptor. Overrides auto-detector proposals;
    /// overridden by user annotations.
    /// </summary>
    Plugin = 2,

    /// <summary>
    /// User manual edit (right-click → mark / redirect / suppress).
    /// Ultimate ground truth — overrides every lower source.
    /// </summary>
    User = 3,
}
