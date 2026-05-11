// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Semantics;

/// <summary>
/// On-disk shape of <c>bowire.schema-hints.json</c> (project file) and
/// <c>~/.bowire/schema-hints.json</c> (user file). The two files share
/// the same schema by design — the frame-semantics-framework ADR pins
/// them to be byte-for-byte the same format so a user-local file can be
/// promoted into a project file with a copy.
/// </summary>
/// <remarks>
/// <para>
/// The format groups annotations under <c>(service, method)</c> first,
/// then by discriminator value under the <c>"types"</c> map (with
/// <c>"*"</c> as the literal wildcard for single-type methods). Each
/// terminal entry is a <c>json-path → semantic-kind</c> string pair.
/// Reads tolerate unknown keys at every level so future framework
/// extensions don't break older readers.
/// </para>
/// </remarks>
public sealed class SchemaHintsFile
{
    /// <summary>The current on-disk format version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// File-format version. Readers refuse versions newer than
    /// <see cref="CurrentVersion"/>; older versions are upgraded
    /// in-place on next write.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    /// <summary>One entry per <c>(service, method)</c> pair.</summary>
    [JsonPropertyName("schemas")]
    public IList<SchemaHintsEntry> Schemas { get; init; } = [];
}

/// <summary>
/// Annotations contributed by a single <c>(service, method)</c> pair —
/// the second-level grouping under <see cref="SchemaHintsFile.Schemas"/>.
/// </summary>
public sealed class SchemaHintsEntry
{
    /// <summary>Service identifier (e.g. <c>"dis.LiveExercise"</c>).</summary>
    [JsonPropertyName("service")]
    public string Service { get; set; } = "";

    /// <summary>Method identifier (e.g. <c>"Subscribe"</c>).</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    /// <summary>
    /// Optional discriminator declaration for multi-type methods.
    /// Absent means single-type (every annotation lives under the
    /// <c>"*"</c> wildcard key in <see cref="Types"/>).
    /// </summary>
    [JsonPropertyName("discriminator")]
    public SchemaHintsDiscriminator? Discriminator { get; set; }

    /// <summary>
    /// Per-discriminator-value annotation maps. The outer key is the
    /// discriminator value (e.g. <c>"EntityStatePdu"</c>) or <c>"*"</c>
    /// for single-type methods; the inner key is a JSONPath; the inner
    /// value is the semantic kind-string.
    /// </summary>
    [JsonPropertyName("types")]
    public IDictionary<string, IDictionary<string, string>> Types { get; init; }
        = new Dictionary<string, IDictionary<string, string>>(StringComparer.Ordinal);
}

/// <summary>
/// Discriminator declaration as serialised in
/// <see cref="SchemaHintsEntry.Discriminator"/>. Exactly one of
/// <see cref="WirePath"/> / <see cref="JsonPath"/> / <see cref="Oneof"/>
/// is expected to be non-null per entry; readers tolerate multiple
/// fields and pick the first non-null in that order.
/// </summary>
public sealed class SchemaHintsDiscriminator
{
    /// <summary>Wire-bytes offset expression (e.g. <c>"byte[1]"</c>).</summary>
    [JsonPropertyName("wirePath")]
    public string? WirePath { get; set; }

    /// <summary>JSONPath evaluated after decode (e.g. <c>"$.type"</c>).</summary>
    [JsonPropertyName("jsonPath")]
    public string? JsonPath { get; set; }

    /// <summary>Protobuf <c>oneof</c> group name.</summary>
    [JsonPropertyName("oneof")]
    public string? Oneof { get; set; }

    /// <summary>
    /// Optional type-registry hint identifying the named enum / table
    /// that maps discriminator values to message types (e.g.
    /// <c>"dis.PduType"</c>). The framework does not interpret this
    /// field in Phase 1 — it is round-tripped through the file
    /// untouched so plugin-side detectors can consult it later.
    /// </summary>
    [JsonPropertyName("registry")]
    public string? Registry { get; set; }
}
