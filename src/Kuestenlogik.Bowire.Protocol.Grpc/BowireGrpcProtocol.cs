// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.Grpc;

/// <summary>
/// gRPC protocol plugin for Bowire. Discovers services via gRPC Server Reflection
/// and invokes methods using dynamic protobuf marshalling.
/// <para>
/// Implements <see cref="IBowireProtocolServices"/> so that
/// <c>builder.Services.AddBowire()</c> automatically registers
/// <c>AddGrpcReflection()</c> and <c>app.MapBowire()</c> maps
/// <c>MapGrpcReflectionService()</c> — no manual setup required.
/// </para>
/// </summary>
public sealed class BowireGrpcProtocol : IBowireProtocol, IBowireProtocolServices, IBowireStreamingWithWireBytes
{
    // Captured during Initialize() so the gRPC channel constructions below
    // can pull the localhost-cert opt-in (Bowire:TrustLocalhostCert) into
    // the SocketsHttpHandler's SslOptions callback. mTLS-protected gRPC
    // servers stay on the dedicated MtlsHandlerOwner path, untouched.
    private IConfiguration? _configuration;

    public string Name => "gRPC";
    public string Id => "grpc";

    /// <summary>
    /// Metadata key callers use to opt into gRPC-Web transport for a single
    /// call. Value is "web" (case-insensitive) for gRPC-Web, anything else
    /// (or absence) for the default native HTTP/2 gRPC. Symmetric to the
    /// <c>grpcweb@</c> URL hint, which Bowire's discovery + invoke endpoints
    /// translate into this header before dispatching to the plugin.
    /// <para>
    /// Spelled <c>X-Bowire-Grpc-Transport</c> so it travels safely through
    /// every HTTP intermediary as a regular request header — gRPC strips
    /// HTTP/2 pseudo-headers, but custom <c>x-</c>-prefixed entries pass
    /// through both native gRPC trailers and gRPC-Web's HTTP framing.
    /// </para>
    /// </summary>
    public const string TransportMetadataKey = "X-Bowire-Grpc-Transport";

    public void Initialize(IServiceProvider? serviceProvider)
    {
        _configuration = serviceProvider?.GetService<IConfiguration>();
    }

    // Community single-path gRPC mark (vectorlogo.zone). Matches the
    // protocol card on the marketing site.
    public string IconSvg => """<svg viewBox="0 0 64 64" fill="currentColor" width="16" height="16" aria-hidden="true"><path d="M27.125 36.16v12.46c0 2.593.44 4.847 1.323 6.762s2.044 3.506 3.485 4.773 3.06 2.225 4.853 2.873S40.39 64 42.212 64s3.632-.324 5.426-.972 3.412-1.606 4.853-2.873 2.603-2.858 3.485-4.773 1.323-4.17 1.323-6.762V27.86l-16.126-.025-.02 8.325H48.3v12.46c0 2.18-.603 3.786-1.8 4.818s-2.632 1.547-4.28 1.547-3.073-.516-4.28-1.547-1.8-2.637-1.8-4.818V15.38c0-2.18.603-3.786 1.8-4.818s2.632-1.547 4.28-1.547 3.073.516 4.28 1.547 1.8 2.638 1.8 4.818v3.182h9V15.38c0-2.534-.44-4.773-1.323-6.718s-2.044-3.55-3.485-4.818S49.432 1.62 47.64.97 44.035 0 42.212 0s-3.632.324-5.426.972-3.412 1.606-4.853 2.873-2.603 2.873-3.485 4.818-1.323 4.184-1.323 6.718v12.46h-9.207v-9.28h2.824l-7.02-9.92-7.02 9.92h2.92V32c0 2.298 1.857 4.16 4.15 4.16h13.355z" fill-rule="evenodd"/></svg>""";

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct)
    {
        // Empty serverUrl is the standalone-CLI-without-flags case
        // (`bowire` with no --url, no schema). Returning empty here
        // is much better than spinning up a GrpcReflectionClient
        // against the empty string and waiting ~10 s for a doomed
        // HTTP/2 handshake — that's what made the standalone first-
        // run land on a hung "Loading services…" spinner.
        if (string.IsNullOrWhiteSpace(serverUrl)) return [];

        // Pull the transport mode (if any) out of the URL — the discovery
        // endpoint stitches __bowireGrpcTransport=web onto URLs that came in
        // via the `grpcweb@` hint because DiscoverAsync has no metadata bag.
        var (cleanUrl, transportMode) = GrpcChannelBuilder.ExtractTransportFromUrl(serverUrl);

        // Discovery doesn't currently carry per-environment metadata, so
        // mTLS-protected gRPC servers can't be reflected against today.
        // Once IBowireProtocol.DiscoverAsync grows a metadata parameter
        // (planned alongside SSE-MCP / streamable discovery), the same
        // MtlsConfig.TryParseFromMetadata path below kicks in here too.
        using var client = new GrpcReflectionClient(
            cleanUrl, showInternalServices, mtlsConfig: null,
            configuration: _configuration, transportMode: transportMode);
        return await client.ListServicesAsync(ct);
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata, CancellationToken ct)
    {
        var mtlsConfig = MtlsConfig.TryParseFromMetadata(metadata);
        var transportMode = GrpcChannelBuilder.ResolveMode(metadata);
        var afterMtls = mtlsConfig is null ? metadata : MtlsConfig.StripMarker(metadata);
        var sanitisedMetadata = GrpcChannelBuilder.StripTransportMarker(afterMtls);
        using var reflectionClient = new GrpcReflectionClient(
            serverUrl, showInternalServices, mtlsConfig, _configuration, transportMode);
        using var invoker = new GrpcInvoker(
            serverUrl, reflectionClient, mtlsConfig, _configuration, transportMode);
        return await invoker.InvokeUnaryAsync(service, method, jsonMessages, sanitisedMetadata, ct);
    }

    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var mtlsConfig = MtlsConfig.TryParseFromMetadata(metadata);
        var transportMode = GrpcChannelBuilder.ResolveMode(metadata);
        var afterMtls = mtlsConfig is null ? metadata : MtlsConfig.StripMarker(metadata);
        var sanitisedMetadata = GrpcChannelBuilder.StripTransportMarker(afterMtls);
        using var reflectionClient = new GrpcReflectionClient(
            serverUrl, showInternalServices, mtlsConfig, _configuration, transportMode);
        using var invoker = new GrpcInvoker(
            serverUrl, reflectionClient, mtlsConfig, _configuration, transportMode);
        await foreach (var frame in invoker.InvokeStreamingWithFramesAsync(service, method, jsonMessages, sanitisedMetadata, ct))
            yield return frame.Json;
    }

    /// <summary>
    /// Binary-aware server-streaming invocation. Yields each frame's wire
    /// bytes alongside its JSON rendering so the Bowire recorder can
    /// persist the binary payload per frame — Phase-2d gRPC-streaming mock
    /// replay needs the original bytes to re-emit without runtime
    /// JSON→protobuf re-encoding.
    /// </summary>
    public async IAsyncEnumerable<StreamFrame> InvokeStreamWithFramesAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var mtlsConfig = MtlsConfig.TryParseFromMetadata(metadata);
        var transportMode = GrpcChannelBuilder.ResolveMode(metadata);
        var afterMtls = mtlsConfig is null ? metadata : MtlsConfig.StripMarker(metadata);
        var sanitisedMetadata = GrpcChannelBuilder.StripTransportMarker(afterMtls);
        using var reflectionClient = new GrpcReflectionClient(
            serverUrl, showInternalServices, mtlsConfig, _configuration, transportMode);
        using var invoker = new GrpcInvoker(
            serverUrl, reflectionClient, mtlsConfig, _configuration, transportMode);
        await foreach (var frame in invoker.InvokeStreamingWithFramesAsync(service, method, jsonMessages, sanitisedMetadata, ct))
            yield return frame;
    }

    public async Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata, CancellationToken ct)
    {
        return await GrpcBowireChannel.CreateAsync(
            serverUrl, service, method, showInternalServices, metadata, ct, _configuration);
    }

    // ---- IBowireProtocolServices ----

    /// <summary>
    /// Registers gRPC Reflection services so Bowire can discover gRPC services
    /// automatically. Called by <c>builder.Services.AddBowire()</c>.
    /// Idempotent — safe to call even if the host already called <c>AddGrpcReflection()</c>.
    /// </summary>
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddGrpcReflection();
    }

    /// <summary>
    /// Maps the gRPC Reflection endpoint so clients (including Bowire's own
    /// discovery) can query the server's service descriptors at runtime.
    /// Called by <c>app.MapBowire()</c>.
    /// </summary>
    public void MapDiscoveryEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcReflectionService();
    }
}
