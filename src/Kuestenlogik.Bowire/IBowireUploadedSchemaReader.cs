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

    /// <summary>
    /// Whether a JSON or YAML document has <paramref name="key"/> at its top
    /// level: the look <see cref="IBowireUploadedSchemaReader.LooksLikeUploadedSchema"/>
    /// needs for the formats that name themselves there (<c>asyncapi</c>,
    /// <c>openrpc</c>).
    /// </summary>
    /// <remarks>
    /// A guess from the head of the document, not a parse: JSON is walked
    /// property by property at depth one, YAML is searched for the key at
    /// the start of a line. Neither reader is needed for it, and a document
    /// that does not parse simply does not have the key.
    /// </remarks>
    public static bool HasTopLevelKey(ReadOnlyMemory<byte> document, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var span = document.Span;
        if (span.StartsWith("﻿"u8)) span = span[3..];
        var start = 0;
        while (start < span.Length && span[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') start++;
        if (start == span.Length) return false;

        if (span[start] == (byte)'{') return JsonHasTopLevelKey(span[start..], key);
        if (span[start] is (byte)'<' or (byte)'[') return false;

        var text = System.Text.Encoding.UTF8.GetString(span[..Math.Min(span.Length, 256 * 1024)]);
        foreach (var line in text.Split('\n'))
        {
            var entry = line.AsSpan().TrimEnd('\r');
            // Top level: no indentation. Quoted keys are legal YAML.
            if (entry.Length == 0 || entry[0] is ' ' or '\t' or '#' or '-') continue;
            var colon = entry.IndexOf(':');
            if (colon <= 0) continue;
            var name = entry[..colon].Trim().Trim('"').Trim('\'');
            if (name.Equals(key, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool JsonHasTopLevelKey(ReadOnlySpan<byte> json, string key)
    {
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(json, new System.Text.Json.JsonReaderOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            while (reader.Read())
            {
                if (reader.TokenType == System.Text.Json.JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                {
                    if (reader.ValueTextEquals(key)) return true;
                    // Past the value without descending into it.
                    reader.Skip();
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON after all, so not a document with this key.
        }
        return false;
    }

    /// <summary>
    /// The local name and namespace of an XML document's root element, or
    /// null when the document is not XML: the look for the formats that are
    /// told apart there (<c>definitions</c> for WSDL, <c>Edmx</c> for an
    /// OData model).
    /// </summary>
    public static (string LocalName, string Namespace)? XmlRoot(ReadOnlyMemory<byte> document)
    {
        try
        {
            using var stream = new MemoryStream(document.ToArray(), writable: false);
            using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
            {
                // A schema file is data from outside: no DTD, no external
                // entity, nothing fetched while looking at it.
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            return reader.MoveToContent() == System.Xml.XmlNodeType.Element
                ? (reader.LocalName, reader.NamespaceURI)
                : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}
