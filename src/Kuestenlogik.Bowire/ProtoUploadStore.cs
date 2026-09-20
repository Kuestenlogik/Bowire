// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire;

/// <summary>
/// The proto files uploaded through the UI, and the services parsed out of
/// them. Services discovered this way are merged into the service list.
/// </summary>
/// <remarks>
/// The documents themselves live in <see cref="SchemaUploadStore"/> since
/// #654 — per identity, per workspace, on disk. What stays here is the parse,
/// which is the part worth not repeating.
/// </remarks>
internal static class ProtoUploadStore
{
    private static readonly Lock CacheLock = new();
    private static string? _cachedFor;
    private static List<BowireServiceInfo>? _cachedServices;

    /// <summary>
    /// Store a proto file and return the services it, and everything uploaded
    /// before it, describe.
    /// </summary>
    public static List<BowireServiceInfo> AddAndParse(string protoContent, string? sourceName = null)
    {
        SchemaUploadStore.Add(SchemaUploadStore.ProtoKind, protoContent, sourceName);
        return GetServices();
    }

    /// <summary>
    /// Get all services discovered from uploaded proto files.
    /// </summary>
    /// <remarks>
    /// The cache is keyed on the directory plus the ids it holds, not merely
    /// held until someone remembers to invalidate it. Anything that changes
    /// the answer — a different identity, a different workspace, an upload
    /// from the CLI while this process is running — changes the key, which is
    /// the property the old process-wide list could not have.
    /// </remarks>
    public static List<BowireServiceInfo> GetServices()
    {
        var docs = SchemaUploadStore.GetAll(SchemaUploadStore.ProtoKind);
        var key = SchemaUploadStore.RootPath() + "|" + string.Join(",", docs.Select(d => d.Id));

        lock (CacheLock)
        {
            if (_cachedServices is not null && string.Equals(_cachedFor, key, StringComparison.Ordinal))
                return _cachedServices;

            var services = new List<BowireServiceInfo>();
            foreach (var doc in docs)
                services.AddRange(ProtoFileParser.Parse(doc.Content));

            _cachedFor = key;
            _cachedServices = services;
            return services;
        }
    }

    /// <summary>
    /// Whether any proto files have been uploaded.
    /// </summary>
    public static bool HasUploads => SchemaUploadStore.Has(SchemaUploadStore.ProtoKind);

    /// <summary>
    /// Clear all uploaded protos, leaving any OpenAPI documents in place.
    /// </summary>
    public static void Clear()
    {
        SchemaUploadStore.Clear(SchemaUploadStore.ProtoKind);
        lock (CacheLock)
        {
            _cachedFor = null;
            _cachedServices = null;
        }
    }
}
