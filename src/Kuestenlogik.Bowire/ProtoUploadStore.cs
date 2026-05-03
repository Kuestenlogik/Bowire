// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire;

/// <summary>
/// In-memory store for proto files uploaded via the UI.
/// Services discovered from uploaded protos are merged into the service list.
/// </summary>
internal static class ProtoUploadStore
{
    private static readonly Lock SyncLock = new();
    private static readonly List<string> UploadedProtoContents = [];
    private static List<BowireServiceInfo>? _cachedServices;

    /// <summary>
    /// Add a proto file content string and invalidate the cache.
    /// </summary>
    public static List<BowireServiceInfo> AddAndParse(string protoContent)
    {
        lock (SyncLock)
        {
            UploadedProtoContents.Add(protoContent);
            _cachedServices = null;
            return GetServices();
        }
    }

    /// <summary>
    /// Get all services discovered from uploaded proto files.
    /// </summary>
    public static List<BowireServiceInfo> GetServices()
    {
        lock (SyncLock)
        {
            if (_cachedServices is not null)
                return _cachedServices;

            var services = new List<BowireServiceInfo>();
            foreach (var content in UploadedProtoContents)
                services.AddRange(ProtoFileParser.Parse(content));

            _cachedServices = services;
            return _cachedServices;
        }
    }

    /// <summary>
    /// Whether any proto files have been uploaded.
    /// </summary>
    public static bool HasUploads
    {
        get
        {
            lock (SyncLock)
                return UploadedProtoContents.Count > 0;
        }
    }

    /// <summary>
    /// Clear all uploaded protos.
    /// </summary>
    public static void Clear()
    {
        lock (SyncLock)
        {
            UploadedProtoContents.Clear();
            _cachedServices = null;
        }
    }
}
