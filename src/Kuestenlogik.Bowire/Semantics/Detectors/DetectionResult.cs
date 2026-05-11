// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics.Detectors;

/// <summary>
/// One proposed annotation produced by a detector — the addressing
/// half (<see cref="Key"/>) plus the semantic claim
/// (<see cref="Semantic"/>). Always lands in the
/// <see cref="AnnotationSource.Auto"/> layer; the
/// <see cref="LayeredAnnotationStore"/> resolver does the priority
/// arithmetic at lookup time.
/// </summary>
/// <param name="Key">
/// Four-dimensional address of the field this annotation applies to —
/// composed by the detector from the
/// <see cref="DetectionContext"/> service/method/message-type plus the
/// JSON path it walked to find the field.
/// </param>
/// <param name="Semantic">
/// The semantic kind the detector claims for the field. Conventionally
/// one of the <see cref="BuiltInSemanticTags"/> catalogue entries.
/// </param>
public readonly record struct DetectionResult(
    AnnotationKey Key,
    SemanticTag Semantic);
