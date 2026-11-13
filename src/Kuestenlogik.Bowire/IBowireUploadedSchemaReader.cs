// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire;

/// <summary>
/// A protocol plugin that can read an uploaded binary schema core cannot
/// (#991).
/// </summary>
/// <remarks>
/// <para>
/// Core stores what the workbench uploads and merges it into discovery, so
/// the workbench, <c>bowire discover</c> and the MCP tool agree on what a URL
/// offers. For a <c>.proto</c> it can do the reading itself. A compiled
/// descriptor set needs the protobuf runtime, which core does not reference
/// and should not start to: every host that embeds Bowire would carry it.
/// </para>
/// <para>
/// So the plugin that owns the format reads it, and core asks through this
/// interface: once when a document is uploaded, to refuse one that is not
/// what its name says, and again when discovery lists what is stored.
/// </para>
/// </remarks>
public interface IBowireUploadedSchemaReader
{
    /// <summary>The stored kind this reader understands, e.g. <c>protoset</c>.</summary>
    string UploadedSchemaKind { get; }

    /// <summary>
    /// The services one stored document describes, in the shape the plugin's
    /// own discovery returns them.
    /// </summary>
    /// <exception cref="FormatException">The bytes are not a document of this kind.</exception>
    IReadOnlyList<BowireServiceInfo> ReadUploadedSchema(ReadOnlyMemory<byte> document);
}
