// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics.Detectors;

/// <summary>
/// The "learn-as-new-types-arrive" hook that connects the SSE / invoke
/// stream to the auto-detector layer. Wire the prober into the frame
/// pipeline; it decides whether the current
/// <see cref="DetectionContext"/> triple is novel and, if so, runs
/// every registered <see cref="IBowireFieldDetector"/> once against
/// the frame and writes the proposals to the
/// <see cref="AnnotationSource.Auto"/> layer.
/// </summary>
/// <remarks>
/// <para>
/// Step 7 of the ADR's <c>"Lifecycle of a binding"</c> is the key
/// correctness property: already-classified <c>(service, method,
/// message-type)</c> triples are NOT re-evaluated. The prober's
/// internal triple-set guarantees that — a single
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.TryAdd"/>
/// lookup is the entire hot-path cost for an already-known triple.
/// </para>
/// <para>
/// Implementations MUST be thread-safe. Two concurrent SSE streams
/// (each running their own server-streaming dispatch) may invoke
/// <see cref="ObserveFrame"/> simultaneously, including against the
/// same triple.
/// </para>
/// </remarks>
public interface IFrameProber
{
    /// <summary>
    /// Inspect a single frame in the context of its
    /// <c>(service, method, message-type)</c> triple. Cheap-no-op
    /// for already-probed triples; runs every detector once and
    /// writes the proposals into the auto layer for novel triples.
    /// </summary>
    void ObserveFrame(in DetectionContext ctx);
}
