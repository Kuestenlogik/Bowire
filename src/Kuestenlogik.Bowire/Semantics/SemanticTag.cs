// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics;

/// <summary>
/// Strongly-typed wrapper around the namespaced kind-string that names
/// the semantic meaning of a schema field (<c>"coordinate.latitude"</c>,
/// <c>"image.bytes"</c>, …).
/// </summary>
/// <remarks>
/// <para>
/// The set of valid values is open — extension authors register their own
/// kinds at runtime — so this type does not constrain to a closed enum.
/// What it does provide is type-safety on the wire (no random
/// <see cref="string"/> can be confused for a semantic tag) and a single
/// catalogue of the well-known kinds the framework ships with under
/// <see cref="BuiltInSemanticTags"/>.
/// </para>
/// <para>
/// The special tag <see cref="BuiltInSemanticTags.None"/> (kind-string
/// <c>"none"</c>) is a real value, not a separate suppression flag: the
/// resolver treats it as "the user explicitly said this is NOT
/// something" and lets it override any auto-detector proposal beneath.
/// <see cref="IsNone"/> distinguishes the suppression case from any
/// other tag.
/// </para>
/// </remarks>
/// <param name="Kind">
/// Namespaced kind-string. Conventionally dotted
/// (<c>family.subkind</c>) but the framework only requires a non-empty
/// case-sensitive identifier; the dot is convention, not syntax.
/// </param>
public sealed record SemanticTag(string Kind)
{
    /// <summary>
    /// True when this tag is the explicit-suppression value
    /// <see cref="BuiltInSemanticTags.None"/>.
    /// </summary>
    public bool IsNone => string.Equals(Kind, BuiltInSemanticTags.NoneKind, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override string ToString() => Kind;
}

/// <summary>
/// Catalogue of well-known semantic kinds the framework ships with.
/// Extensions register their own kinds at runtime and don't appear
/// here — this list is the contract between the core and the
/// built-in detectors / viewers documented in the
/// frame-semantics-framework ADR.
/// </summary>
public static class BuiltInSemanticTags
{
    /// <summary>The raw <c>"none"</c> kind-string.</summary>
    public const string NoneKind = "none";

    // ------------------------------------------------------------
    // Suppression
    // ------------------------------------------------------------

    /// <summary>
    /// Explicit suppression — "this field carries NO recognised
    /// semantic." Higher-priority sources win over lower-priority
    /// proposals; a user-supplied <see cref="None"/> overrides a
    /// plugin- or auto-detector-supplied coordinate tag.
    /// </summary>
    public static SemanticTag None { get; } = new(NoneKind);

    // ------------------------------------------------------------
    // Coordinates
    // ------------------------------------------------------------

    /// <summary>WGS84 latitude in decimal degrees, range <c>[-90, 90]</c>.</summary>
    public static SemanticTag CoordinateLatitude { get; } = new("coordinate.latitude");

    /// <summary>WGS84 longitude in decimal degrees, range <c>[-180, 180]</c>.</summary>
    public static SemanticTag CoordinateLongitude { get; } = new("coordinate.longitude");

    /// <summary>Earth-Centred-Earth-Fixed X coordinate (metres).</summary>
    public static SemanticTag CoordinateEcefX { get; } = new("coordinate.ecef.x");

    /// <summary>Earth-Centred-Earth-Fixed Y coordinate (metres).</summary>
    public static SemanticTag CoordinateEcefY { get; } = new("coordinate.ecef.y");

    /// <summary>Earth-Centred-Earth-Fixed Z coordinate (metres).</summary>
    public static SemanticTag CoordinateEcefZ { get; } = new("coordinate.ecef.z");

    // ------------------------------------------------------------
    // Image
    // ------------------------------------------------------------

    /// <summary>Raw image bytes (PNG, JPEG, …).</summary>
    public static SemanticTag ImageBytes { get; } = new("image.bytes");

    /// <summary>Mime-type companion for <see cref="ImageBytes"/>.</summary>
    public static SemanticTag ImageMimeType { get; } = new("image.mime-type");

    // ------------------------------------------------------------
    // Audio
    // ------------------------------------------------------------

    /// <summary>Raw audio bytes (WAV, Opus, …).</summary>
    public static SemanticTag AudioBytes { get; } = new("audio.bytes");

    /// <summary>Sample-rate companion for <see cref="AudioBytes"/>.</summary>
    public static SemanticTag AudioSampleRate { get; } = new("audio.sample-rate");

    // ------------------------------------------------------------
    // Time series / tabular
    // ------------------------------------------------------------

    /// <summary>Timestamp field on a time-series record.</summary>
    public static SemanticTag TimeseriesTimestamp { get; } = new("timeseries.timestamp");

    /// <summary>Value field on a time-series record.</summary>
    public static SemanticTag TimeseriesValue { get; } = new("timeseries.value");

    /// <summary>Array-of-records grid payload.</summary>
    public static SemanticTag TableRowArray { get; } = new("table.row-array");
}
