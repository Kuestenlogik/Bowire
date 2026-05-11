// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics;

/// <summary>
/// One semantic annotation: a tagged claim that the field addressed by
/// <see cref="Key"/> carries the meaning <see cref="Semantic"/>, and that
/// the claim originates from <see cref="Source"/>.
/// </summary>
/// <remarks>
/// <para>
/// Annotations are the single data model the entire frame-semantics
/// framework runs on. Detectors propose them; plugins ship them as part
/// of their Discovery-Descriptor; users edit them; viewers and editors
/// consume them.
/// </para>
/// <para>
/// Equality is value-based (record default). Two annotations from
/// different sources can therefore coexist in a single store without
/// clobbering one another — the store keys them by
/// <c>(Key, Source)</c> internally and applies the resolution priority
/// at lookup time.
/// </para>
/// </remarks>
/// <param name="Key">Four-dimensional address of the annotated field.</param>
/// <param name="Semantic">The semantic kind this field carries.</param>
/// <param name="Source">Origin of the annotation, used for resolution priority.</param>
public sealed record Annotation(
    AnnotationKey Key,
    SemanticTag Semantic,
    AnnotationSource Source);
