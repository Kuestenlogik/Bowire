// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire;

/// <summary>
/// The OpenAPI / Swagger documents uploaded through the UI. The REST plugin
/// reads from here during discovery and merges the parsed services with
/// whatever it finds via embedded discovery or URL fetching.
///
/// The store keeps raw document text only — parsing happens in the REST plugin
/// because <see cref="Kuestenlogik.Bowire"/> core can't take a dependency on the OpenAPI
/// reader package without dragging it into every host that uses Bowire.
/// </summary>
/// <remarks>
/// Since #654 the documents live in <see cref="SchemaUploadStore"/>: on disk,
/// in the identity's slot, under the workspace being served. This type stays
/// as the plugin-facing surface, unchanged in shape — a protocol plugin ships
/// on its own cadence and should not have to move because core changed where
/// it puts a file.
/// </remarks>
public static class OpenApiUploadStore
{
    /// <summary>Adds a raw document and returns the assigned id.</summary>
    public static string Add(string content, string? sourceName = null)
        => SchemaUploadStore.Add(SchemaUploadStore.OpenApiKind, content, sourceName);

    /// <summary>Returns a snapshot of all currently stored documents.</summary>
    public static IReadOnlyList<UploadedDoc> GetAll()
        => SchemaUploadStore.GetAll(SchemaUploadStore.OpenApiKind)
            .Select(d => new UploadedDoc(d.Id, d.Content, d.SourceName))
            .ToArray();

    /// <summary>True when at least one document has been uploaded.</summary>
    public static bool HasUploads => SchemaUploadStore.Has(SchemaUploadStore.OpenApiKind);

    /// <summary>
    /// Removes all uploaded documents, leaving any uploaded protos in place.
    /// </summary>
    public static void Clear() => SchemaUploadStore.Clear(SchemaUploadStore.OpenApiKind);
}

/// <summary>A single uploaded OpenAPI/Swagger document with its raw content.</summary>
public sealed record UploadedDoc(string Id, string Content, string SourceName);
