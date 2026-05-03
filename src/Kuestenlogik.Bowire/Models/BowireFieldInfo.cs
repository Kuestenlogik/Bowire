// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Models;

/// <summary>
/// Describes a single field within a message. Originally modeled after
/// protobuf, extended with an optional Source annotation so REST parameters
/// from different locations (path, query, header, body) can share the same
/// shape as protobuf fields.
/// </summary>
public sealed record BowireFieldInfo(
    string Name,
    int Number,
    string Type,
    string Label,
    bool IsMap,
    bool IsRepeated,
    BowireMessageInfo? MessageType,
    List<BowireEnumValue>? EnumValues)
{
    /// <summary>
    /// Where this field travels in a REST request: "path", "query", "header",
    /// or "body". Null for non-REST protocols.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>True if the source schema marks this field as required.</summary>
    public bool Required { get; init; }

    /// <summary>Human-readable description from the source schema. Optional.</summary>
    public string? Description { get; init; }

    /// <summary>Example value from the source schema, used to pre-fill the form. Stored as JSON-serializable string.</summary>
    public string? Example { get; init; }

    /// <summary>
    /// True for REST <c>multipart/form-data</c> fields whose schema declares
    /// <c>format: binary</c> (file upload). The frontend renders a file
    /// picker instead of a text input, the form value travels as base64 in
    /// the JSON envelope, and <c>RestInvoker</c> decodes it back into a
    /// <see cref="StreamContent"/> on the multipart wire.
    /// </summary>
    public bool IsBinary { get; init; }
}
