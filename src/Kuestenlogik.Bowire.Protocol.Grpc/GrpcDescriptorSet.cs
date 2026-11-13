// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.Grpc;

/// <summary>
/// A caller-supplied <c>FileDescriptorSet</c>, carried inline in the request
/// metadata (#653).
/// </summary>
/// <remarks>
/// <para>
/// The wire format is a compiled descriptor set — what
/// <c>protoc --descriptor_set_out=api.protoset --include_imports</c> produces,
/// and what <c>grpcurl -protoset</c> consumes. Not <c>.proto</c> source: the
/// parser in this repository (<c>ProtoFileParser</c>) is regex-based and
/// extracts names for display, which is enough to fill a sidebar and nowhere
/// near enough to marshal a message. Compiling <c>.proto</c> text at runtime
/// would mean shipping a protobuf compiler; a descriptor set is the same
/// information already compiled, and it is the format the ecosystem hands
/// around for precisely this purpose.
/// </para>
/// <para>
/// <c>--include_imports</c> is not optional advice. The builder resolves
/// cross-references between the files it is given, so a set without its
/// transitive imports produces a descriptor that cannot be built — and the
/// failure surfaces as a marshalling error far from the missing import.
/// </para>
/// <para>
/// Carried through metadata under a magic key, the way
/// <see cref="Kuestenlogik.Bowire.Auth.MtlsConfig"/> carries client
/// certificates. That keeps every plugin signature unchanged and puts the
/// decision where the caller already passes per-call configuration.
/// </para>
/// </remarks>
internal static class GrpcDescriptorSet
{
    /// <summary>
    /// Metadata key carrying the descriptor set. Plugins strip it before
    /// forwarding the rest of the metadata as gRPC headers.
    /// </summary>
    public const string MarkerKey = BowireMetadataKeys.GrpcDescriptorSet;

    /// <summary>
    /// Read the marker and load the set, or <c>null</c> when it is absent.
    /// </summary>
    /// <remarks>
    /// A malformed or unreadable marker returns <c>null</c> rather than
    /// throwing, so the call falls through to reflection — the behaviour every
    /// caller had before this existed. A descriptor set that cannot be read is
    /// a reason to try the server, not a reason to fail before asking it.
    /// </remarks>
    public static IReadOnlyList<FileDescriptorProto>? TryLoadFromMetadata(
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return null;
        if (!metadata.TryGetValue(MarkerKey, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var bytes = ReadBytes(raw);
            if (bytes is null || bytes.Length == 0) return null;

            var set = FileDescriptorSet.Parser.ParseFrom(bytes);
            return set.File.Count > 0 ? set.File.ToList() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidProtocolBufferException
            or IOException or UnauthorizedAccessException or FormatException or NotSupportedException)
        {
            _ = ex;
            return null;
        }
    }

    /// <summary>Strip the marker so it never reaches the wire as a header.</summary>
    public static Dictionary<string, string>? StripMarker(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return null;
        var copy = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var (k, v) in metadata)
        {
            if (string.Equals(k, MarkerKey, StringComparison.Ordinal)) continue;
            copy[k] = v;
        }
        return copy;
    }

    /// <summary>Build the marker value for a descriptor set on disk.</summary>
    public static string MarkerForPath(string path)
        => JsonSerializer.Serialize(new DescriptorMarker { Path = path });

    /// <summary>
    /// The marker is JSON with either a path or inline bytes.
    /// </summary>
    /// <remarks>
    /// Both, because the two callers differ: a CLI or CI run names a file it
    /// can read, while anything arriving from a browser has bytes and no path
    /// the server could open. Supporting only one would have made the other
    /// caller invent a temporary file.
    /// </remarks>
    private static byte[]? ReadBytes(string raw)
    {
        var trimmed = raw.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            // A bare value is a path — the shape a hand-written scan invocation
            // produces, and not worth demanding JSON for.
            return File.Exists(raw) ? File.ReadAllBytes(raw) : null;
        }

        var marker = JsonSerializer.Deserialize<DescriptorMarker>(raw, s_markerJson);
        if (marker is null) return null;

        if (!string.IsNullOrWhiteSpace(marker.Base64)) return Convert.FromBase64String(marker.Base64);
        if (!string.IsNullOrWhiteSpace(marker.Path) && File.Exists(marker.Path)) return File.ReadAllBytes(marker.Path);
        return null;
    }

    /// <summary>
    /// The descriptor source for one call: the caller's set when they supplied
    /// one, the server otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A supplied set wins. It is an explicit instruction, and honouring it
    /// avoids a doomed reflection round trip on every single call against the
    /// servers this feature exists for — the ones with reflection switched off.
    /// It is also what <c>grpcurl</c> does with <c>-protoset</c>, so the
    /// behaviour will not surprise anyone arriving from there.
    /// </para>
    /// <para>
    /// I had written the opposite into the acceptance criteria on #653
    /// ("reflection still wins when it answers"). That would have made every
    /// call on a reflection-less server pay a timeout first, and would have let
    /// a server silently override the descriptors an operator deliberately
    /// handed us. The cost is that a stale set produces marshalling errors
    /// where reflection would have been right — which is a consequence of
    /// supplying a stale set, and visible in a way the timeout would not be.
    /// </para>
    /// </remarks>
    public static IGrpcDescriptorSource CreateSource(
        IReadOnlyDictionary<string, string>? metadata,
        string serverUrl,
        bool showInternalServices,
        Kuestenlogik.Bowire.Auth.MtlsConfig? mtlsConfig,
        Microsoft.Extensions.Configuration.IConfiguration? configuration,
        GrpcTransportMode transportMode = GrpcTransportMode.Native,
        IReadOnlyList<FileDescriptorProto>? hostFiles = null)
    {
        var supplied = TryLoadFromMetadata(metadata);
        if (supplied is not null) return new GrpcDescriptorSetSource(supplied, showInternalServices);

        var known = Merge(hostFiles, LoadUploaded());
        return known.Count == 0
            ? new GrpcReflectionClient(serverUrl, showInternalServices, mtlsConfig, configuration, transportMode)
            : new GrpcUploadedDescriptorSource(
                known, showInternalServices,
                () => new GrpcReflectionClient(serverUrl, showInternalServices, mtlsConfig, configuration, transportMode));
    }

    /// <summary>
    /// The embedding host's own descriptors (#992) before the uploaded ones,
    /// a file name counted once.
    /// </summary>
    /// <remarks>
    /// The host's come first because they are compiled into the process that
    /// serves the calls: where an uploaded set names the same file, the
    /// host's copy is the one the server actually runs.
    /// </remarks>
    private static IReadOnlyList<FileDescriptorProto> Merge(
        IReadOnlyList<FileDescriptorProto>? hostFiles, IReadOnlyList<FileDescriptorProto> uploaded)
    {
        if (hostFiles is null || hostFiles.Count == 0) return uploaded;
        if (uploaded.Count == 0) return hostFiles;

        var names = hostFiles.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        return [.. hostFiles, .. uploaded.Where(f => names.Add(f.Name))];
    }

    /// <summary>
    /// One serialized <c>FileDescriptorProto</c>, read with the extension
    /// registry reflection uses so its google.api.http annotations survive.
    /// </summary>
    public static FileDescriptorProto ParseFile(ByteString serialized)
        => s_fileParser.ParseFrom(serialized);

    private static readonly MessageParser<FileDescriptorProto> s_fileParser =
        FileDescriptorProto.Parser.WithExtensionRegistry(new ExtensionRegistry
        {
            Google.Api.AnnotationsExtensions.Http,
        });

    /// <summary>
    /// Parse one descriptor set as it was uploaded or named on a command line.
    /// </summary>
    /// <exception cref="FormatException">
    /// The bytes are not a <c>FileDescriptorSet</c>, the set is empty, or it
    /// lacks an import one of its files names.
    /// </exception>
    /// <remarks>
    /// The import check is here because the failure it prevents is far from
    /// its cause: a set compiled without <c>--include_imports</c> parses, lists
    /// its services, and then cannot marshal the first message that uses an
    /// imported type. Refusing it at the door says which file is missing while
    /// the person still has the <c>protoc</c> line in front of them.
    /// </remarks>
    public static List<FileDescriptorProto> Parse(ReadOnlySpan<byte> bytes)
    {
        FileDescriptorSet set;
        try
        {
            set = s_setParser.ParseFrom(bytes);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new FormatException(
                "Not a compiled descriptor set. Build one with "
                + "`protoc --descriptor_set_out=api.protoset --include_imports`; a .proto source file gives names only.",
                ex);
        }

        if (set.File.Count == 0)
            throw new FormatException(
                "The descriptor set holds no files. Build one with "
                + "`protoc --descriptor_set_out=api.protoset --include_imports`.");

        var files = set.File.ToList();
        if (GrpcInvoker.BuildFileDescriptors(files).Count != files.Count)
        {
            var present = files.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            var missing = files.SelectMany(f => f.Dependency).Where(d => !present.Contains(d))
                .Distinct(StringComparer.Ordinal).ToList();
            throw new FormatException(missing.Count > 0
                ? $"The descriptor set lacks {string.Join(", ", missing)}. Compile it with `--include_imports`."
                : "The descriptor set does not resolve: its files refer to types none of them defines.");
        }

        return files;
    }

    // The extension registry reflection uses, so an uploaded set keeps its
    // google.api.http annotations the same way.
    private static readonly MessageParser<FileDescriptorSet> s_setParser =
        FileDescriptorSet.Parser.WithExtensionRegistry(new ExtensionRegistry
        {
            Google.Api.AnnotationsExtensions.Http,
        });

    private static readonly Lock s_uploadedLock = new();
    private static string? s_uploadedFor;
    private static IReadOnlyList<FileDescriptorProto> s_uploaded = [];

    /// <summary>
    /// The files of every descriptor set stored for this call's workspace, or
    /// handed to this command with <c>--schema</c> (#991).
    /// </summary>
    /// <remarks>
    /// One list across all sets, a file name counted once: two sets compiled
    /// from one tree both carry the shared imports, and the builder refuses a
    /// file it is given twice. The first upload of a name wins. Cached on the
    /// directory plus the ids, the way core caches its parse of the .proto
    /// uploads; a set that no longer reads is skipped.
    /// </remarks>
    internal static IReadOnlyList<FileDescriptorProto> LoadUploaded()
    {
        var docs = SchemaUploadStore.GetAllBytes(SchemaUploadStore.ProtoSetKind);
        if (docs.Count == 0) return [];
        var key = SchemaUploadStore.RootPath() + "|" + string.Join(",", docs.Select(d => d.Id));

        lock (s_uploadedLock)
        {
            if (string.Equals(s_uploadedFor, key, StringComparison.Ordinal)) return s_uploaded;

            var files = new List<FileDescriptorProto>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var doc in docs)
            {
                try
                {
                    foreach (var file in Parse(doc.Bytes))
                    {
                        if (names.Add(file.Name)) files.Add(file);
                    }
                }
                catch (FormatException)
                {
                    // Skipped, per the remarks.
                }
            }

            s_uploadedFor = key;
            s_uploaded = files;
            return files;
        }
    }

    /// <summary>
    /// Case-insensitive on purpose.
    /// </summary>
    /// <remarks>
    /// System.Text.Json matches property names case-sensitively by default, so
    /// the natural <c>{"base64": "..."}</c> a caller writes bound to nothing and
    /// the marker silently read as absent — the call then fell through to
    /// reflection and failed on a server that has none, with a message about
    /// descriptors that gave no hint the set had been handed over and ignored.
    /// Caught by the integration test on the first run; it would have been
    /// nearly invisible in review.
    /// </remarks>
    private static readonly JsonSerializerOptions s_markerJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class DescriptorMarker
    {
        public string? Path { get; set; }
        public string? Base64 { get; set; }
    }
}

/// <summary>
/// Serves descriptors out of a set the caller supplied, instead of asking the
/// server for them (#653).
/// </summary>
internal sealed class GrpcDescriptorSetSource(
    IReadOnlyList<FileDescriptorProto> files, bool showInternalServices = false) : IGrpcDescriptorSource
{
    private readonly IReadOnlyList<FileDescriptorProto> _files = files;
    private readonly bool _showInternalServices = showInternalServices;

    /// <summary>
    /// The whole set, whatever service is asked for.
    /// </summary>
    /// <remarks>
    /// Returning everything rather than walking the import graph for the one
    /// service is deliberate. A descriptor set built with
    /// <c>--include_imports</c> already *is* the closure, the builder
    /// topologically sorts what it is handed, and a caller who supplied a set
    /// containing more than one service meant all of it to be callable.
    /// Trimming here would only create a way to be subtly wrong.
    /// </remarks>
    public Task<List<FileDescriptorProto>> ResolveAllDescriptorsAsync(
        string serviceName, CancellationToken ct = default)
    {
        _ = serviceName;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_files.ToList());
    }

    /// <summary>
    /// Every service in the set, in the same shape reflection produces.
    /// </summary>
    /// <remarks>
    /// <paramref name="ct"/> is honoured but nothing here awaits: the bytes
    /// were parsed before this object existed, so the work is a walk over
    /// descriptors already in memory. It stays async because the interface has
    /// to accommodate the implementation that really does go to the network.
    /// </remarks>
    public Task<List<BowireServiceInfo>> ListServicesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var all = _files.ToList();
        var services = new List<BowireServiceInfo>();

        foreach (var fd in all)
        {
            foreach (var svc in fd.Service)
            {
                var fullName = string.IsNullOrEmpty(fd.Package) ? svc.Name : $"{fd.Package}.{svc.Name}";
                if (!_showInternalServices && GrpcReflectionClient.InternalServices.Contains(fullName))
                    continue;

                var info = GrpcReflectionClient.BuildServiceInfo(fullName, all);
                if (info is not null) services.Add(info);
            }
        }

        return Task.FromResult(services.OrderBy(s => s.Name, StringComparer.Ordinal).ToList());
    }

    /// <summary>Nothing to release — the bytes were read before this existed.</summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// The descriptors Bowire already holds for the services they describe, the
/// server for the rest: the uploaded descriptor sets (#991) and, embedded,
/// the host's own compiled descriptors (#992).
/// </summary>
/// <remarks>
/// <para>
/// An uploaded set is stored for a workspace, not for one server, and a
/// workspace can hold a server with reflection beside one without. An
/// embedding host's descriptors describe that host, and its workbench can
/// still be pointed at another server. So the held descriptors answer for the
/// services they name and nothing else: a call to any other service still
/// asks its server, as it did before.
/// </para>
/// <para>
/// For a service it does name, the set wins without asking, for the reason
/// given on <see cref="GrpcDescriptorSet.CreateSource"/>: the servers this
/// exists for have reflection off, and asking first would cost every call a
/// round trip that cannot succeed.
/// </para>
/// </remarks>
internal sealed class GrpcUploadedDescriptorSource(
    IReadOnlyList<FileDescriptorProto> uploaded,
    bool showInternalServices,
    Func<IGrpcDescriptorSource> createReflection) : IGrpcDescriptorSource
{
    private readonly GrpcDescriptorSetSource _uploaded = new(uploaded, showInternalServices);
    private readonly HashSet<string> _described = uploaded
        .SelectMany(f => f.Service.Select(s => string.IsNullOrEmpty(f.Package) ? s.Name : $"{f.Package}.{s.Name}"))
        .ToHashSet(StringComparer.Ordinal);
    private IGrpcDescriptorSource? _reflection;

    // Created on first use: a call the set answers never opens a channel
    // for reflection.
    private IGrpcDescriptorSource Reflection => _reflection ??= createReflection();

    public Task<List<FileDescriptorProto>> ResolveAllDescriptorsAsync(
        string serviceName, CancellationToken ct = default)
        => _described.Contains(serviceName)
            ? _uploaded.ResolveAllDescriptorsAsync(serviceName, ct)
            : Reflection.ResolveAllDescriptorsAsync(serviceName, ct);

    /// <summary>
    /// The services of the uploaded sets, then whatever else the server
    /// admits to.
    /// </summary>
    /// <remarks>
    /// A server that does not answer reflection is the expected case here,
    /// not a failure: the held services are the answer, and reporting the
    /// refusal instead would hide them behind the very error they exist to
    /// get past. That includes an embedding host, which no longer maps a
    /// reflection endpoint unless asked to. Cancellation is still
    /// cancellation.
    /// </remarks>
    public async Task<List<BowireServiceInfo>> ListServicesAsync(CancellationToken ct = default)
    {
        var services = await _uploaded.ListServicesAsync(ct);
        try
        {
            var taken = services.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var svc in await Reflection.ListServicesAsync(ct))
            {
                if (taken.Add(svc.Name)) services.Add(svc);
            }
        }
        catch (Exception ex) when (ex is global::Grpc.Core.RpcException or HttpRequestException
            or InvalidOperationException or IOException)
        {
            ct.ThrowIfCancellationRequested();
        }

        return services;
    }

    public void Dispose()
    {
        _reflection?.Dispose();
        _uploaded.Dispose();
    }
}
