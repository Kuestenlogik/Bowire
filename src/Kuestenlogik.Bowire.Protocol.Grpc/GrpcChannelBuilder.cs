// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Grpc.Net.Client;
using Grpc.Net.Client.Web;

namespace Kuestenlogik.Bowire.Protocol.Grpc;

/// <summary>
/// Transport variant the gRPC plugin should use for a given call. Selected by
/// the caller via the <c>grpcweb@</c> URL hint or the
/// <see cref="BowireGrpcProtocol.TransportMetadataKey"/> metadata header.
/// <para>
/// Native is the historical path: HTTP/2 with gRPC framing, the only variant
/// the plugin ever knew. Web wraps the inner <see cref="HttpMessageHandler"/>
/// with <see cref="GrpcWebHandler"/> so calls travel as gRPC-Web (base64 or
/// length-prefixed binary inside HTTP/1.1 + HTTP/2 messages) — required for
/// services that only expose gRPC-Web behind an L7 proxy (Envoy, browser-
/// fronted backends, Rheinmetall's TacticalAPI on its 4268 port).
/// </para>
/// </summary>
public enum GrpcTransportMode
{
    Native = 0,
    Web    = 1
}

/// <summary>
/// Single point of channel construction for the gRPC plugin. Both reflection
/// + invoke paths and the duplex channel route through here so the
/// "wrap with <see cref="GrpcWebHandler"/> when mode is Web" decision lives
/// in exactly one place. Native mode is identical to what
/// <c>GrpcChannel.ForAddress(...)</c> did before this helper existed, so the
/// historical Native path stays bit-for-bit unchanged.
/// </summary>
internal static class GrpcChannelBuilder
{
    /// <summary>
    /// Query-string marker the discovery endpoint appends to a server URL
    /// when the user typed <c>grpcweb@&lt;url&gt;</c>. <see cref="IBowireProtocol.DiscoverAsync"/>
    /// takes no metadata dict, so we piggy-back on the URL instead — symmetric
    /// to the <c>__bowireQuery__</c> / <c>__bowireMtls__</c> metadata markers.
    /// The plugin strips this parameter before handing the URL to
    /// <c>GrpcChannel.ForAddress</c> so the gRPC stack never sees it.
    /// </summary>
    public const string TransportUrlMarker = "__bowireGrpcTransport";
    /// <summary>
    /// Build a <see cref="GrpcChannel"/> for the given transport mode.
    /// The <paramref name="innerHandler"/> is the inner pipeline (typically
    /// a <see cref="SocketsHttpHandler"/> built by <c>BowireHttpClientFactory</c>
    /// or an mTLS-wrapped <see cref="SocketsHttpHandler"/> from
    /// <c>MtlsHandlerOwner</c>); in Web mode it becomes the inner of a
    /// <see cref="GrpcWebHandler"/>, in Native mode it's used as-is.
    /// </summary>
    public static GrpcChannel BuildChannel(
        string serverUrl,
        HttpMessageHandler innerHandler,
        GrpcTransportMode mode)
    {
        HttpMessageHandler effectiveHandler = mode switch
        {
            // GrpcWebMode.GrpcWebText is base64 over HTTP/1.1 and is the only
            // variant that supports client-streaming / duplex (the binary
            // GrpcWeb mode requires trailers, which HTTP/1.1 can't carry).
            // Defaulting to GrpcWebText keeps streaming working when the
            // server supports it without forcing callers to think about it;
            // servers that only speak binary gRPC-Web still accept text
            // requests because the content-type negotiation flows through
            // the Accept / Content-Type headers GrpcWebHandler sets.
            GrpcTransportMode.Web => new GrpcWebHandler(GrpcWebMode.GrpcWebText, innerHandler),
            _ => innerHandler
        };

        var options = new GrpcChannelOptions
        {
            HttpHandler = effectiveHandler,
            DisposeHttpClient = false  // owned by the caller / by an MtlsHandlerOwner
        };

        if (mode == GrpcTransportMode.Web)
        {
            // GrpcChannel.ForAddress otherwise sends every request as HTTP/2.
            // gRPC-Web is HTTP/1.1 (and HTTP/2 + WebSocket for advanced
            // duplex which we don't enable here), so pin the version to 1.1
            // and ask the policy to never upgrade. Servers that happen to
            // speak both still work — they just see an HTTP/1.1 request.
            options.HttpVersion = System.Net.HttpVersion.Version11;
            options.HttpVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact;
        }

        return GrpcChannel.ForAddress(serverUrl, options);
    }

    /// <summary>
    /// Resolve the transport mode from a metadata bag. Reads the
    /// <see cref="BowireGrpcProtocol.TransportMetadataKey"/> entry
    /// case-insensitively (the JS layer normalises to PascalCase but
    /// hand-written HTTP clients shouldn't have to). Anything other than
    /// "web" (literal, case-insensitive) — including null, absent, "native",
    /// or an empty string — falls back to <see cref="GrpcTransportMode.Native"/>.
    /// </summary>
    public static GrpcTransportMode ResolveMode(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return GrpcTransportMode.Native;
        foreach (var (k, v) in metadata)
        {
            if (string.Equals(k, BowireGrpcProtocol.TransportMetadataKey, StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(v, "web", StringComparison.OrdinalIgnoreCase)
                    ? GrpcTransportMode.Web
                    : GrpcTransportMode.Native;
            }
        }
        return GrpcTransportMode.Native;
    }

    /// <summary>
    /// Pull the transport mode out of a server URL and return the URL with
    /// the <see cref="TransportUrlMarker"/> query parameter stripped. Used
    /// by <c>DiscoverAsync</c>, which has no metadata dict to consult.
    /// Falls back to <see cref="GrpcTransportMode.Native"/> when the marker
    /// is absent or unrecognised. Other query parameters survive intact.
    /// </summary>
    public static (string CleanUrl, GrpcTransportMode Mode) ExtractTransportFromUrl(string serverUrl)
    {
        if (string.IsNullOrEmpty(serverUrl)) return (serverUrl, GrpcTransportMode.Native);

        var queryIdx = serverUrl.IndexOf('?', StringComparison.Ordinal);
        if (queryIdx < 0) return (serverUrl, GrpcTransportMode.Native);

        var headPart = serverUrl[..queryIdx];
        var queryPart = serverUrl[(queryIdx + 1)..];
        var pairs = queryPart.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var mode = GrpcTransportMode.Native;
        var kept = new List<string>(pairs.Length);
        foreach (var pair in pairs)
        {
            var eqIdx = pair.IndexOf('=', StringComparison.Ordinal);
            var name = eqIdx < 0 ? pair : pair[..eqIdx];
            if (string.Equals(name, TransportUrlMarker, StringComparison.Ordinal))
            {
                var value = eqIdx < 0 ? "" : Uri.UnescapeDataString(pair[(eqIdx + 1)..]);
                if (string.Equals(value, "web", StringComparison.OrdinalIgnoreCase))
                    mode = GrpcTransportMode.Web;
                // The marker itself is dropped from the rebuilt query.
                continue;
            }
            kept.Add(pair);
        }
        var cleanUrl = kept.Count == 0
            ? headPart
            : headPart + "?" + string.Join('&', kept);
        return (cleanUrl, mode);
    }

    /// <summary>
    /// Returns a copy of the metadata dict with the transport marker
    /// removed — plugins call this before forwarding metadata as gRPC
    /// request headers so the marker doesn't show up in the server's
    /// trailers / leak through to downstream services. Returns null when
    /// the input is null or empty.
    /// </summary>
    public static Dictionary<string, string>? StripTransportMarker(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return null;
        Dictionary<string, string>? copy = null;
        foreach (var (k, v) in metadata)
        {
            if (string.Equals(k, BowireGrpcProtocol.TransportMetadataKey, StringComparison.OrdinalIgnoreCase))
                continue;
            copy ??= new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
            copy[k] = v;
        }
        return copy;
    }
}
