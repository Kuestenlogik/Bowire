// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Which schema kinds this host imports, which kind an uploaded file is, and
/// the services the stored documents describe (#991, #994).
/// </summary>
/// <remarks>
/// <para>
/// Two kinds are core's own: a <c>.proto</c>, which core parses, and an
/// OpenAPI document, which is stored raw and parsed by the REST plugin while
/// it discovers. Every other kind is declared by a loaded plugin through
/// <see cref="IBowireUploadedSchemaReader"/>, and this type is the one place
/// that knows about all of them: the upload endpoint, the file picker and
/// discovery ask here.
/// </para>
/// <para>
/// The bytes live in <see cref="SchemaUploadStore"/>, per identity and per
/// workspace. A kind whose plugin is not loaded stays stored and lists
/// nothing.
/// </para>
/// </remarks>
internal static class UploadedSchemaImports
{
    private static readonly BowireUploadedSchemaKind s_proto =
        new(SchemaUploadStore.ProtoKind, "Protobuf source (names only)", [".proto"]);

    private static readonly BowireUploadedSchemaKind s_openApi =
        new(SchemaUploadStore.OpenApiKind, "OpenAPI / Swagger", [".json", ".yaml", ".yml"]);

    private const string RestPluginId = "rest";

    private static readonly Lock CacheLock = new();
    private static string? _cachedFor;
    private static List<BowireServiceInfo>? _cachedServices;

    /// <summary>One importable kind and who reads it.</summary>
    /// <param name="Kind">The declaration.</param>
    /// <param name="PluginId">
    /// The plugin a document of this kind is discovered through; <c>proto</c>
    /// for core's own parser.
    /// </param>
    /// <param name="Reader">The plugin that reads it; null for core's two kinds.</param>
    internal sealed record Importable(
        BowireUploadedSchemaKind Kind, string PluginId, IBowireUploadedSchemaReader? Reader);

    /// <summary>
    /// Every kind this host imports: the plugins' kinds in load order, framed
    /// by core's two.
    /// </summary>
    /// <remarks>
    /// OpenAPI is listed only with the REST plugin loaded. Without it the
    /// document would be stored and never read, which is the failure this
    /// listing exists to stop offering.
    /// </remarks>
    public static IReadOnlyList<Importable> Kinds(BowireProtocolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var kinds = new List<Importable> { new(s_proto, SchemaUploadStore.ProtoKind, null) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { s_proto.Kind, s_openApi.Kind };
        foreach (var protocol in registry.Protocols)
        {
            if (protocol is not IBowireUploadedSchemaReader reader) continue;
            foreach (var kind in reader.UploadedSchemaKinds)
            {
                // The first plugin to declare a kind keeps it: two readers
                // for one stored kind would make discovery list it twice.
                if (seen.Add(kind.Kind)) kinds.Add(new Importable(kind, protocol.Id, reader));
            }
        }

        if (registry.Protocols.Any(p => string.Equals(p.Id, RestPluginId, StringComparison.OrdinalIgnoreCase)))
            kinds.Add(new Importable(s_openApi, RestPluginId, null));
        return kinds;
    }

    /// <summary>The importable kind named <paramref name="kind"/>, if this host has it.</summary>
    public static Importable? Find(BowireProtocolRegistry registry, string kind)
        => Kinds(registry).FirstOrDefault(k => string.Equals(k.Kind.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The kind an uploaded file is, or null with the reason it is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The extension decides when exactly one kind declares it: that kind's
    /// reader then says what is wrong with a file that does not read, which
    /// is more use than "not recognised".
    /// </para>
    /// <para>
    /// When several declare it, the plugins are asked to look at the
    /// document, in load order. <c>.json</c> and <c>.yaml</c> are the case:
    /// OpenAPI, AsyncAPI and OpenRPC all arrive under them. OpenAPI is the
    /// fallback there, as it was before any plugin could claim a file,
    /// because it is the one kind nobody is asked about at upload: it is
    /// stored raw and read during discovery.
    /// </para>
    /// <para>
    /// An extension nobody declares is offered to every plugin the same way,
    /// so a WSDL saved as <c>.xml</c> or SDL saved as <c>.txt</c> still finds
    /// its reader. A file nobody takes is refused with the list of what can
    /// be uploaded here.
    /// </para>
    /// </remarks>
    public static Importable? Route(
        BowireProtocolRegistry registry, string? fileName, ReadOnlyMemory<byte> document, out string? refusal)
    {
        refusal = null;
        var kinds = Kinds(registry);
        var extension = Path.GetExtension(fileName ?? string.Empty);

        List<Importable> declaring = extension.Length == 0
            ? []
            : [.. kinds.Where(k => k.Kind.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))];
        if (declaring.Count == 1) return declaring[0];

        IReadOnlyList<Importable> asked = declaring.Count > 1 ? declaring : kinds;
        foreach (var candidate in asked)
        {
            if (candidate.Reader is { } reader && Recognises(reader, candidate.Kind.Kind, document))
                return candidate;
        }

        if (declaring.FirstOrDefault(k => k.Reader is null) is { } fallback) return fallback;

        var named = string.IsNullOrEmpty(fileName) ? "The file" : fileName;
        refusal = declaring.Count > 1
            ? $"{named} is none of the formats read from {extension} here: "
                + string.Join(", ", declaring.Select(k => k.Kind.DisplayName)) + "."
            : $"{named} is not a schema this host imports. It reads: {Describe(kinds)}.";
        return null;
    }

    private static bool Recognises(IBowireUploadedSchemaReader reader, string kind, ReadOnlyMemory<byte> document)
    {
        try
        {
            return reader.LooksLikeUploadedSchema(kind, document);
        }
#pragma warning disable CA1031 // A sniff is a guess, and one that throws has guessed no.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>The importable kinds as a sentence fragment: name and extensions.</summary>
    public static string Describe(IReadOnlyList<Importable> kinds)
        => string.Join("; ", kinds.Select(k => $"{k.Kind.DisplayName} ({string.Join(", ", k.Kind.Extensions)})"));

    /// <summary>Store one document of a plugin's kind.</summary>
    public static string Add(Importable kind, byte[] bytes, string? sourceName)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return SchemaUploadStore.AddBytes(
            kind.Kind.Kind, bytes, sourceName, kind.Kind.Extensions.Count > 0 ? kind.Kind.Extensions[0] : null);
    }

    /// <summary>
    /// The services every stored document of a plugin's kind describes.
    /// </summary>
    /// <remarks>
    /// Cached on the directory plus the ids it holds, and the returned
    /// objects are the cache: copy before annotating, as with
    /// <see cref="ProtoUploadStore.GetServices"/>. A stored document that no
    /// longer reads is skipped; it was refused at upload if it never did.
    /// </remarks>
    public static List<BowireServiceInfo> GetServices(BowireProtocolRegistry registry)
    {
        var read = new List<(Importable Kind, IReadOnlyList<SchemaUploadStore.StoredBinarySchema> Docs)>();
        foreach (var kind in Kinds(registry))
        {
            if (kind.Reader is null) continue;
            var docs = SchemaUploadStore.GetAllBytes(kind.Kind.Kind);
            if (docs.Count > 0) read.Add((kind, docs));
        }
        if (read.Count == 0) return [];

        var key = SchemaUploadStore.RootPath() + "|"
            + string.Join(";", read.Select(r => r.Kind.Kind.Kind + ":" + string.Join(",", r.Docs.Select(d => d.Id))));

        lock (CacheLock)
        {
            if (_cachedServices is not null && string.Equals(_cachedFor, key, StringComparison.Ordinal))
                return _cachedServices;

            var services = new List<BowireServiceInfo>();
            // Per plugin, not across them: a "Query" read from SDL and a
            // "Query" read from another format are two services.
            var seen = new HashSet<(string Plugin, string Name)>();
            foreach (var (kind, docs) in read)
            {
                foreach (var doc in docs)
                {
                    try
                    {
                        foreach (var svc in kind.Reader!.ReadUploadedSchema(kind.Kind.Kind, doc.Bytes, doc.SourceName))
                        {
                            if (!seen.Add((kind.PluginId, svc.Name))) continue;
                            // What the probe stamps on a plugin's own
                            // findings: the workbench routes a call by it,
                            // and these never pass through a probe.
                            svc.Source = kind.PluginId;
                            services.Add(svc);
                        }
                    }
                    catch (FormatException)
                    {
                        // Skipped, per the remarks.
                    }
                }
            }

            _cachedFor = key;
            _cachedServices = services;
            return services;
        }
    }

    /// <summary>Whether any document of a plugin's kind is stored.</summary>
    public static bool HasUploads(BowireProtocolRegistry registry)
        => Kinds(registry).Any(k => k.Reader is not null && SchemaUploadStore.Has(k.Kind.Kind));

    /// <summary>Remove every stored document of one kind, leaving the others in place.</summary>
    public static void Clear(string kind)
    {
        SchemaUploadStore.Clear(kind);
        Forget();
    }

    /// <summary>Remove every stored document of every kind.</summary>
    public static void ClearAll()
    {
        SchemaUploadStore.ClearAll();
        // Its parse cache is keyed on what is stored, so it notices by
        // itself; this drops the entry rather than leaving it to be replaced.
        ProtoUploadStore.Clear();
        Forget();
    }

    private static void Forget()
    {
        lock (CacheLock)
        {
            _cachedFor = null;
            _cachedServices = null;
        }
    }
}
