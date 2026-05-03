// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.Sse;

/// <summary>
/// Bowire protocol plugin for Server-Sent Events (SSE).
/// Discovers SSE endpoints via <see cref="SseEndpointAttribute"/>, <c>Produces("text/event-stream")</c>
/// metadata, or manual registration. Auto-discovered by <see cref="BowireProtocolRegistry"/>.
/// Also implements <see cref="IInlineSseSubscriber"/> so other plugins (MCP
/// notifications, GraphQL graphql-sse subscriptions) can reuse the SSE
/// event-stream parser via <see cref="BowireProtocolRegistry.FindSseSubscriber"/>
/// without taking a compile-time dependency on this assembly.
/// </summary>
public sealed class BowireSseProtocol : IBowireProtocol, IInlineSseSubscriber
{
    private static readonly List<SseEndpointInfo> s_registeredEndpoints = [];
    private IServiceProvider? _serviceProvider;

    public string Name => "SSE";
    public string Id => "sse";

    // SSE has no official logo; one-way broadcast glyph matches the site card.
    public string IconSvg => """<svg viewBox="0 0 24 24" fill="none" stroke="#fb923c" stroke-width="1.5" width="16" height="16" aria-hidden="true"><path d="M5 8h14l-4-4"/><path d="M5 12h10"/><path d="M5 16h6"/></svg>""";

    /// <summary>
    /// Endpoints registered statically via <see cref="RegisterEndpoint"/>.
    /// </summary>
    internal static IReadOnlyList<SseEndpointInfo> RegisteredEndpoints => s_registeredEndpoints;

    /// <summary>
    /// Register an SSE endpoint for Bowire discovery.
    /// Call before <c>MapBowire()</c> or during startup.
    /// </summary>
    public static void RegisterEndpoint(SseEndpointInfo endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        s_registeredEndpoints.Add(endpoint);
    }

    /// <summary>
    /// Clears all statically registered endpoints. Primarily for testing.
    /// </summary>
    internal static void ClearRegisteredEndpoints() => s_registeredEndpoints.Clear();

    /// <inheritdoc />
    public void Initialize(IServiceProvider? serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        var services = SseEndpointDiscovery.Discover(s_registeredEndpoints, _serviceProvider);
        return Task.FromResult(services);
    }

    /// <inheritdoc />
    public Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        // SSE is streaming-only — unary invocation is not applicable
        return Task.FromResult(new InvokeResult(
            null, 0,
            "SSE endpoints are streaming only. Use the streaming view to subscribe.",
            new Dictionary<string, string>()));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = ResolveUrl(serverUrl, method, jsonMessages);

        await using var subscriber = new SseSubscriber();
        await foreach (var evt in subscriber.SubscribeAsync(url, metadata, ct))
            yield return evt;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> SubscribeAsync(
        string url,
        Dictionary<string, string>? headers,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Cross-plugin entry point — open a brand-new SseSubscriber so the
        // caller's lifetime is independent of any other in-flight stream.
        await using var subscriber = new SseSubscriber();
        await foreach (var evt in subscriber.SubscribeAsync(url, headers, ct))
            yield return evt;
    }

    /// <inheritdoc />
    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        // SSE is unidirectional (server -> client), no interactive channel
        return Task.FromResult<IBowireChannel?>(null);
    }

    /// <summary>
    /// Resolves the SSE endpoint URL from the method's full name and optional JSON body override.
    /// The method FullName is encoded as "SSE{path}" (e.g., "SSE/events/ticker").
    /// </summary>
    private static string ResolveUrl(string serverUrl, string method, List<string> jsonMessages)
    {
        // Extract path from the method full name (format: "SSE/events/ticker")
        var path = method.StartsWith("SSE", StringComparison.Ordinal)
            ? method[3..]
            : method;

        var url = serverUrl.TrimEnd('/') + path;

        // Allow URL override from the request body
        if (jsonMessages.Count > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonMessages[0]);
                if (doc.RootElement.TryGetProperty("url", out var urlProp))
                {
                    var customUrl = urlProp.GetString();
                    if (!string.IsNullOrEmpty(customUrl))
                    {
                        url = customUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            ? customUrl
                            : serverUrl.TrimEnd('/') + customUrl;
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed JSON — ignore and use the default URL
            }
        }

        return url;
    }
}
