// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;

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
    public const string MarkerKey = "__bowireGrpcDescriptors__";

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
        GrpcTransportMode transportMode = GrpcTransportMode.Native)
    {
        var supplied = TryLoadFromMetadata(metadata);
        return supplied is not null
            ? new GrpcDescriptorSetSource(supplied)
            : new GrpcReflectionClient(serverUrl, showInternalServices, mtlsConfig, configuration, transportMode);
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
internal sealed class GrpcDescriptorSetSource(IReadOnlyList<FileDescriptorProto> files) : IGrpcDescriptorSource
{
    private readonly IReadOnlyList<FileDescriptorProto> _files = files;

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

    /// <summary>Nothing to release — the bytes were read before this existed.</summary>
    public void Dispose()
    {
    }
}
