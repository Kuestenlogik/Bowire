// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire;

/// <summary>
/// A protocol plugin that imports schema documents core cannot read (#991,
/// #994).
/// </summary>
/// <remarks>
/// <para>
/// Core stores what the workbench uploads and merges it into discovery, so
/// the workbench, <c>bowire discover</c> and the MCP tool agree on what a URL
/// offers. For a <c>.proto</c> it can do the reading itself. Every other
/// format belongs to the plugin that speaks its protocol: a compiled
/// descriptor set needs the protobuf runtime, GraphQL SDL a GraphQL parser,
/// an EDMX document the OData model library. Core references none of them
/// and should not start to, because every host that embeds Bowire would
/// carry them.
/// </para>
/// <para>
/// So a plugin declares the kinds it imports, and core asks through this
/// interface: once when a document is uploaded, to refuse one that is not
/// what its name says, and again when discovery lists what is stored. The
/// upload endpoint routes a file to a kind by the extensions declared here;
/// the file picker accepts what the loaded plugins declare.
/// </para>
/// </remarks>
public interface IBowireUploadedSchemaReader
{
    /// <summary>The schema kinds this plugin imports.</summary>
    IReadOnlyList<BowireUploadedSchemaKind> UploadedSchemaKinds { get; }

    /// <summary>
    /// Whether <paramref name="document"/> looks like a document of
    /// <paramref name="kind"/>.
    /// </summary>
    /// <remarks>
    /// Asked only when the file name does not settle the kind: several kinds
    /// declare the extension (<c>.json</c> is OpenAPI, AsyncAPI and OpenRPC),
    /// or none does. A cheap look at the head of the document is enough, e.g.
    /// its top-level key or root element; the full read happens in
    /// <see cref="ReadUploadedSchema"/>. The default says no, which suits a
    /// kind whose extensions are its own.
    /// </remarks>
    bool LooksLikeUploadedSchema(string kind, ReadOnlyMemory<byte> document) => false;

    /// <summary>
    /// The services one document of <paramref name="kind"/> describes, in the
    /// shape the plugin's own discovery returns them.
    /// </summary>
    /// <param name="kind">One of <see cref="UploadedSchemaKinds"/>.</param>
    /// <param name="document">The document as it was uploaded.</param>
    /// <param name="sourceName">The file name it was uploaded under.</param>
    /// <exception cref="FormatException">The bytes are not a document of this kind.</exception>
    IReadOnlyList<BowireServiceInfo> ReadUploadedSchema(
        string kind, ReadOnlyMemory<byte> document, string sourceName);
}

/// <summary>
/// One schema format a plugin imports (#994).
/// </summary>
/// <param name="Kind">
/// The stored kind, e.g. <c>protoset</c> or <c>graphql-sdl</c>. Lower case,
/// unique across plugins; it names the document in the workspace's schema
/// index.
/// </param>
/// <param name="DisplayName">What the workbench calls the format, e.g. <c>GraphQL SDL</c>.</param>
/// <param name="Extensions">
/// The file extensions the format arrives under, with the leading dot and in
/// lower case. The first one is given to a document uploaded without one.
/// </param>
public sealed record BowireUploadedSchemaKind(
    string Kind, string DisplayName, IReadOnlyList<string> Extensions);

/// <summary>One stored schema document, as a plugin reads it back.</summary>
/// <param name="Id">Stable for the stored document; changes when its content does.</param>
/// <param name="Document">The document as it was uploaded.</param>
/// <param name="SourceName">The file name it was uploaded under.</param>
public sealed record BowireUploadedSchema(string Id, ReadOnlyMemory<byte> Document, string SourceName);

/// <summary>
/// The schema documents stored for the workspace being served, for the plugin
/// that imports their kind (#994).
/// </summary>
/// <remarks>
/// Discovery lists a stored document through
/// <see cref="IBowireUploadedSchemaReader"/> without the plugin doing
/// anything. This is for the other half: a call names a service the document
/// describes, and the plugin needs the document again to build it — the
/// selection set of a GraphQL operation, the SOAPAction of an operation.
/// </remarks>
public static class BowireUploadedSchemas
{
    /// <summary>Every stored document of <paramref name="kind"/>, in upload order.</summary>
    public static IReadOnlyList<BowireUploadedSchema> GetAll(string kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        return SchemaUploadStore.GetAllBytes(kind)
            .Select(d => new BowireUploadedSchema(d.Id, d.Bytes, d.SourceName))
            .ToArray();
    }
}
