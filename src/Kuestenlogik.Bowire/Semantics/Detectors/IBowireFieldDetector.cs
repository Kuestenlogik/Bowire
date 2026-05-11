// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Semantics.Detectors;

/// <summary>
/// Server-side rule that proposes one or more
/// <see cref="Annotation"/>s on a sample frame.
/// </summary>
/// <remarks>
/// <para>
/// Built-in detectors land in this same namespace
/// (<see cref="Wgs84CoordinateDetector"/>, <see cref="GeoJsonPointDetector"/>,
/// <see cref="ImageBytesDetector"/>, <see cref="AudioBytesDetector"/>,
/// <see cref="TimestampDetector"/>). Third-party detectors ship through
/// the future <c>[BowireExtension]</c> contract and reuse the same
/// interface.
/// </para>
/// <para>
/// Implementations MUST be stateless / thread-safe — the
/// <see cref="FrameProber"/> may invoke them concurrently. A single
/// detector instance is registered as a singleton; per-frame state
/// belongs inside the local scope of <see cref="Detect"/>.
/// </para>
/// <para>
/// Detectors get the whole decoded frame (not a per-field callback) so
/// paired-field rules can match a <c>lat</c>+<c>lon</c> pair at the
/// same parent path naturally. See <see cref="DetectionContext"/>.
/// </para>
/// </remarks>
public interface IBowireFieldDetector
{
    /// <summary>
    /// Stable id used by logging + the future "registered detectors"
    /// surface. Conventionally a dotted namespace
    /// (<c>kuestenlogik.wgs84-coordinate</c>); not required to be a URI.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Inspect the decoded frame carried by <paramref name="ctx"/> and
    /// emit zero, one, or many proposed annotations. The frame prober
    /// writes them into the <see cref="AnnotationSource.Auto"/> layer
    /// without further filtering — detectors are themselves responsible
    /// for false-positive resistance.
    /// </summary>
    /// <param name="ctx">
    /// Decoded frame + addressing context. Pass-by-readonly-reference
    /// because <see cref="DetectionContext"/> is a
    /// <see langword="readonly"/> <see langword="record"/>
    /// <see langword="struct"/> and we don't want to copy the
    /// <see cref="JsonElement"/> root on every call.
    /// </param>
    IEnumerable<DetectionResult> Detect(in DetectionContext ctx);
}
