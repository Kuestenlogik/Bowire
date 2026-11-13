// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire;

/// <summary>
/// The compiled protobuf descriptor sets uploaded through the workbench, and
/// the services read out of them (#991).
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="ProtoUploadStore"/> for the kind that can be
/// called: a <c>.proto</c> gives names, a descriptor set gives the message
/// layouts a call is marshalled with. The bytes live in
/// <see cref="SchemaUploadStore"/>, per identity and per workspace; the gRPC
/// plugin reads them from there when a call names a service they describe.
/// </para>
/// <para>
/// Core cannot read the format, so listing goes through whichever loaded
/// plugin implements <see cref="IBowireUploadedSchemaReader"/> for it. With
/// none loaded the sets stay stored and list nothing.
/// </para>
/// </remarks>
internal static class ProtoSetUploadStore
{
    private static readonly Lock CacheLock = new();
    private static string? _cachedFor;
    private static List<BowireServiceInfo>? _cachedServices;

    /// <summary>The reader for descriptor sets among the loaded plugins, if any.</summary>
    public static IBowireUploadedSchemaReader? ReaderIn(BowireProtocolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Protocols
            .OfType<IBowireUploadedSchemaReader>()
            .FirstOrDefault(r => string.Equals(
                r.UploadedSchemaKind, SchemaUploadStore.ProtoSetKind, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Store one descriptor set.</summary>
    public static string Add(byte[] bytes, string? sourceName)
        => SchemaUploadStore.AddBytes(SchemaUploadStore.ProtoSetKind, bytes, sourceName);

    /// <summary>
    /// The services every stored descriptor set describes.
    /// </summary>
    /// <remarks>
    /// Cached on the directory plus the ids it holds, and the returned
    /// objects are the cache: copy before annotating, as with
    /// <see cref="ProtoUploadStore.GetServices"/>. A stored set that no longer
    /// reads is skipped; it was refused at upload if it never did.
    /// </remarks>
    public static List<BowireServiceInfo> GetServices(BowireProtocolRegistry registry)
    {
        var reader = ReaderIn(registry);
        if (reader is null) return [];

        var docs = SchemaUploadStore.GetAllBytes(SchemaUploadStore.ProtoSetKind);
        if (docs.Count == 0) return [];
        var key = SchemaUploadStore.RootPath() + "|" + string.Join(",", docs.Select(d => d.Id));

        lock (CacheLock)
        {
            if (_cachedServices is not null && string.Equals(_cachedFor, key, StringComparison.Ordinal))
                return _cachedServices;

            var services = new List<BowireServiceInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var doc in docs)
            {
                try
                {
                    foreach (var svc in reader.ReadUploadedSchema(doc.Bytes))
                    {
                        if (!seen.Add(svc.Name)) continue;
                        // What the probe stamps on a plugin's own findings:
                        // the workbench routes a call by it, and these never
                        // pass through a probe.
                        if (reader is IBowireProtocol protocol) svc.Source = protocol.Id;
                        services.Add(svc);
                    }
                }
                catch (FormatException)
                {
                    // Skipped, per the remarks.
                }
            }

            _cachedFor = key;
            _cachedServices = services;
            return services;
        }
    }

    /// <summary>Whether any descriptor set is stored.</summary>
    public static bool HasUploads => SchemaUploadStore.Has(SchemaUploadStore.ProtoSetKind);

    /// <summary>Remove every stored descriptor set, leaving the other kinds in place.</summary>
    public static void Clear()
    {
        SchemaUploadStore.Clear(SchemaUploadStore.ProtoSetKind);
        lock (CacheLock)
        {
            _cachedFor = null;
            _cachedServices = null;
        }
    }
}
