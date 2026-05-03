// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire;

/// <summary>
/// In-memory store for OpenAPI / Swagger documents uploaded via the UI. The
/// REST plugin reads from this store during discovery and merges the parsed
/// services with whatever it finds via embedded discovery or URL fetching.
///
/// The store keeps raw document text only — parsing happens in the REST plugin
/// because <see cref="Kuestenlogik.Bowire"/> core can't take a dependency on the OpenAPI
/// reader package without dragging it into every host that uses Bowire.
/// </summary>
public static class OpenApiUploadStore
{
    private static readonly Lock SyncLock = new();
    private static readonly List<UploadedDoc> Uploads = [];

    /// <summary>Adds a raw document and returns the assigned id.</summary>
    public static string Add(string content, string? sourceName = null)
    {
        lock (SyncLock)
        {
            var id = "upload_" + DateTime.UtcNow.Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
            Uploads.Add(new UploadedDoc(id, content, sourceName ?? "uploaded"));
            return id;
        }
    }

    /// <summary>Returns a snapshot of all currently stored documents.</summary>
    public static IReadOnlyList<UploadedDoc> GetAll()
    {
        lock (SyncLock)
        {
            return Uploads.ToArray();
        }
    }

    /// <summary>True when at least one document has been uploaded.</summary>
    public static bool HasUploads
    {
        get
        {
            lock (SyncLock)
                return Uploads.Count > 0;
        }
    }

    /// <summary>Removes all uploaded documents.</summary>
    public static void Clear()
    {
        lock (SyncLock)
        {
            Uploads.Clear();
        }
    }
}

/// <summary>A single uploaded OpenAPI/Swagger document with its raw content.</summary>
public sealed record UploadedDoc(string Id, string Content, string SourceName);
