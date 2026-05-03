// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.WebSocket;

/// <summary>
/// Bowire protocol plugin for raw WebSocket connections. Discovers
/// endpoints either from <c>EndpointDataSource</c> entries that carry a
/// <see cref="WebSocketEndpointAttribute"/> (embedded mode) or from manual
/// registration via <see cref="RegisterEndpoint"/>. Invocation goes
/// exclusively through the channel API: there are no unary or
/// server-streaming WebSocket methods.
/// Also implements <see cref="IInlineWebSocketChannel"/> so other plugins
/// (the GraphQL plugin's graphql-transport-ws subscription support) can
/// open WebSocket channels — with sub-protocols + auth headers — without
/// taking a compile-time dependency on this assembly.
/// </summary>
public sealed class BowireWebSocketProtocol : IBowireProtocol, IInlineWebSocketChannel
{
    /// <summary>
    /// Optional metadata key the user can set in the request headers to ask
    /// for one or more WebSocket sub-protocols on the upgrade handshake.
    /// Comma-separated values are split. The key is consumed before being
    /// forwarded as an HTTP header so it doesn't reach the wire as a header.
    /// </summary>
    public const string SubProtocolMetadataKey = "X-Bowire-WebSocket-Subprotocol";

    private static readonly List<WebSocketEndpointInfo> s_registeredEndpoints = [];
    private IServiceProvider? _serviceProvider;

    public string Name => "WebSocket";
    public string Id => "websocket";

    // Community WebSocket logo (gilbarbara/logos set, also on Iconify).
    public string IconSvg => """<svg viewBox="0 0 256 193" fill="currentColor" width="16" height="16" preserveAspectRatio="xMidYMid meet" aria-hidden="true"><path d="M192.440223 144.644612L224.220111 144.644612 224.220111 68.3393384 188.415329 32.5345562 165.943007 55.0068785 192.440223 81.5040943 192.440223 144.644612ZM224.303963 160.576482L178.017688 160.576482 113.451687 160.576482 86.954471 134.079266 98.1906322 122.843105 120.075991 144.728464 165.104487 144.728464 120.746806 100.286931 132.06682 88.9669178 176.4245 133.324599 176.4245 88.2961022 154.622994 66.4945955 165.775303 55.3422863 110.684573 0 56.3485097 0 0 0 31.6960367 31.6960367 31.6960367 31.7798886 31.8637406 31.7798886 97.4359646 31.7798886 120.662954 55.0068785 86.7029152 88.9669178 63.4759253 65.7399279 63.4759253 47.7117589 31.6960367 47.7117589 31.6960367 78.9046839 86.7029152 133.911562 64.3144448 156.300033 100.119227 192.104815 154.45529 192.104815 256 192.104815 224.303963 160.576482Z"/></svg>""";

    public IReadOnlyList<BowirePluginSetting> Settings =>
    [
        new("autoInterpretJson", "Auto-interpret JSON",
            "Parse JSON payloads in text frames for structured display",
            "bool", true),
        new("showBinaryAsHex", "Show binary as hex",
            "Display binary frames as hex dump instead of base64",
            "bool", true)
    ];

    /// <summary>
    /// Endpoints registered statically via <see cref="RegisterEndpoint"/>.
    /// </summary>
    internal static IReadOnlyList<WebSocketEndpointInfo> RegisteredEndpoints => s_registeredEndpoints;

    /// <summary>
    /// Register a WebSocket endpoint for Bowire discovery. Call before
    /// <c>MapBowire()</c> or during startup.
    /// </summary>
    public static void RegisterEndpoint(WebSocketEndpointInfo endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        s_registeredEndpoints.Add(endpoint);
    }

    /// <summary>Clears all statically registered endpoints. Primarily for testing.</summary>
    internal static void ClearRegisteredEndpoints() => s_registeredEndpoints.Clear();

    public void Initialize(IServiceProvider? serviceProvider) => _serviceProvider = serviceProvider;

    public Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        var services = WebSocketEndpointDiscovery.Discover(s_registeredEndpoints, _serviceProvider);

        // Standalone mode: when the user supplied a ws:// or wss:// URL, also
        // expose a synthetic "connect" method so they can open a channel against
        // an arbitrary remote endpoint without registering anything in advance.
        if (services.Count == 0 && IsWebSocketUrl(serverUrl))
        {
            services.Add(BuildAdHocService(serverUrl));
        }

        // Tag every method with the origin URL so /api/channel/open can route
        // back to the right server in multi-URL setups.
        foreach (var svc in services)
            svc.OriginUrl ??= serverUrl;

        return Task.FromResult(services);
    }

    public Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        // WebSocket has no unary semantics — direct everyone at the channel API.
        return Task.FromResult(new InvokeResult(
            null, 0,
            "WebSocket endpoints are channel-only. Use the channel view to send and receive frames.",
            new Dictionary<string, string>()));
    }

    public IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        => AsyncEnumerable.Empty<string>();

    public async Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        var uri = ResolveUri(serverUrl, method);
        if (uri is null) return null;

        // Pull the mTLS marker first so it never leaks onto the WebSocket
        // upgrade request as a literal HTTP header. The cert lands on
        // ClientWebSocketOptions.ClientCertificates instead.
        var mtlsConfig = Kuestenlogik.Bowire.Auth.MtlsConfig.TryParseFromMetadata(metadata);
        var sanitised = mtlsConfig is null ? metadata : Kuestenlogik.Bowire.Auth.MtlsConfig.StripMarker(metadata);

        // Pull the sub-protocol hint out of the metadata dict so it doesn't
        // also leak onto the upgrade request as a literal HTTP header.
        var (headers, subProtocols) = ExtractSubProtocols(sanitised);

        return await WebSocketBowireChannel.CreateAsync(uri, headers, subProtocols, ct, mtlsConfig);
    }

    /// <inheritdoc />
    public async Task<IBowireChannel> OpenAsync(
        string url,
        IReadOnlyList<string>? subProtocols,
        Dictionary<string, string>? headers,
        CancellationToken ct = default)
    {
        // Cross-plugin entry point — used by the GraphQL plugin (and any
        // other future consumer) to open a WebSocket channel without taking
        // a compile-time dependency on the WebSocket plugin.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Invalid WebSocket URL: '{url}'.");

        return await WebSocketBowireChannel.CreateAsync(uri, headers, subProtocols, ct);
    }

    private static (Dictionary<string, string>? Headers, IReadOnlyList<string>? SubProtocols) ExtractSubProtocols(
        Dictionary<string, string>? metadata)
    {
        if (metadata is null) return (null, null);

        // Case-insensitive lookup so users can pass lowercase / mixed case
        // and still hit the magic key.
        string? raw = null;
        string? matchedKey = null;
        foreach (var (k, v) in metadata)
        {
            if (string.Equals(k, SubProtocolMetadataKey, StringComparison.OrdinalIgnoreCase))
            {
                raw = v;
                matchedKey = k;
                break;
            }
        }

        if (matchedKey is null) return (metadata, null);

        var filtered = new Dictionary<string, string>(metadata.Count - 1, StringComparer.Ordinal);
        foreach (var (k, v) in metadata)
        {
            if (!string.Equals(k, matchedKey, StringComparison.Ordinal))
                filtered[k] = v;
        }

        var protos = string.IsNullOrWhiteSpace(raw)
            ? null
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return (filtered, protos);
    }

    private static BowireServiceInfo BuildAdHocService(string serverUrl)
    {
        var input = new BowireMessageInfo("WebSocketFrame", "WebSocketFrame",
        [
            new BowireFieldInfo(
                Name: "data",
                Number: 1,
                Type: "string",
                Label: "optional",
                IsMap: false,
                IsRepeated: false,
                MessageType: null,
                EnumValues: null)
            {
                Source = "body",
                Description = "Raw text frame to send. Send binary frames via the channel UI."
            }
        ]);

        var output = new BowireMessageInfo("WebSocketFrame", "WebSocketFrame", []);
        var path = ExtractPath(serverUrl);

        return new BowireServiceInfo("WebSocket", "websocket",
        [
            new BowireMethodInfo(
                Name: path,
                FullName: "WebSocket" + path,
                ClientStreaming: true,
                ServerStreaming: true,
                InputType: input,
                OutputType: output,
                MethodType: "Duplex")
            {
                Summary = "Connect to " + serverUrl,
                Description = "Ad-hoc WebSocket channel — send raw text frames from the form, or binary frames from the channel UI."
            }
        ])
        {
            Source = "websocket",
            Description = "WebSocket endpoint."
        };
    }

    private static string ExtractPath(string url)
    {
        try
        {
            return new Uri(url).PathAndQuery;
        }
        catch
        {
            return "/";
        }
    }

    private static Uri? ResolveUri(string serverUrl, string method)
    {
        // The method name is the path that discovery emitted. Combine it with
        // the connection's base URL, falling back to the method itself if it
        // already looks like an absolute URL.
        if (Uri.TryCreate(method, UriKind.Absolute, out var absolute) && IsWebSocketScheme(absolute.Scheme))
            return absolute;

        if (string.IsNullOrEmpty(serverUrl)) return null;

        // Convert http(s) → ws(s) for embedded discovery cases where the user
        // typed the regular HTTP origin.
        var baseUrl = serverUrl;
        if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            baseUrl = "ws://" + baseUrl["http://".Length..];
        else if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            baseUrl = "wss://" + baseUrl["https://".Length..];

        // If the base URL already includes a path, prefer it; otherwise append
        // the method name as path.
        try
        {
            var baseUri = new Uri(baseUrl, UriKind.Absolute);

            // Standalone "ad-hoc" case: discovery sets method = baseUri.PathAndQuery
            // — so the base URL is the canonical one, no further joining needed.
            if (baseUri.PathAndQuery == method || method == "/")
                return baseUri;

            return new Uri(baseUri, method);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWebSocketUrl(string url) =>
        !string.IsNullOrEmpty(url) &&
        (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase));

    private static bool IsWebSocketScheme(string scheme) =>
        scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);
}
