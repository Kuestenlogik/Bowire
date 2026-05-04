// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
// CA1001: _http lives for the lifetime of the protocol registry, which is
// the lifetime of the host process. Adding IDisposable to IBowireProtocol
// just to dispose a singleton at shutdown would ripple through every plugin
// without payoff.
#pragma warning disable CA1001
public sealed class BowireSseProtocol : IBowireProtocol, IInlineSseSubscriber
#pragma warning restore CA1001
{
    private static readonly List<SseEndpointInfo> s_registeredEndpoints = [];
    private IServiceProvider? _serviceProvider;

    // Built lazily from BowireHttpClientFactory in Initialize() so the
    // localhost-cert opt-in (Bowire:TrustLocalhostCert) reaches the
    // certificate validation callback. SSE connections are long-lived, so
    // a 1-hour timeout — short enough that an actual broken connection
    // eventually surfaces, long enough that legitimate keep-alive
    // streams (every 5 s) keep the channel open indefinitely.
    private HttpClient _http = new() { Timeout = TimeSpan.FromHours(1) };

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
        var config = serviceProvider?.GetService<IConfiguration>();
        _http = BowireHttpClientFactory.Create(config, Id, TimeSpan.FromHours(1));
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

        await using var subscriber = new SseSubscriber(_http);
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
        await using var subscriber = new SseSubscriber(_http);
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
    private string ResolveUrl(string serverUrl, string method, List<string> jsonMessages)
    {
        // The frontend's invokeStreaming sends method.name (the human-readable
        // label, e.g. 'Slow keep-alive tick…') rather than method.fullName
        // (the route-bearing 'SSE/events/heartbeat'). Run discovery to recover
        // the path the same way /api/services would — covers both manual
        // RegisterEndpoint registrations and Produces("text/event-stream")
        // auto-discovered endpoints. Fall back to the legacy fullName parse
        // for callers that still pass the synthesised 'SSE/...' shape.
        string path;
        var match = SseEndpointDiscovery.Discover(s_registeredEndpoints, _serviceProvider)
            .SelectMany(s => s.Methods)
            .FirstOrDefault(m =>
                string.Equals(m.Name, method, StringComparison.Ordinal) ||
                string.Equals(m.FullName, method, StringComparison.Ordinal));
        if (match is not null)
        {
            // FullName is "SSE{path}" — strip the prefix to get the route.
            path = match.FullName.StartsWith("SSE", StringComparison.Ordinal)
                ? match.FullName[3..]
                : match.FullName;
        }
        else if (method.StartsWith("SSE", StringComparison.Ordinal))
        {
            path = method[3..];
        }
        else
        {
            path = method.StartsWith('/') ? method : "/" + method;
        }

        var url = serverUrl.TrimEnd('/') + path;

        // Allow URL override from the request body. The override is
        // only honoured when it's an absolute http(s) URL or a path
        // anchored at root ('/...'). Bare strings — e.g. the form
        // builder echoes back the schema type name 'string' when the
        // user never typed anything into an optional field — would
        // otherwise concatenate to an invalid URI like
        // 'https://localhost:5114string' and surface as a confusing
        // 'Invalid port specified' on Execute. Ignoring garbage here
        // keeps the default endpoint working out of the box.
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
                        if (customUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            url = customUrl;
                        else if (customUrl.StartsWith('/'))
                            url = serverUrl.TrimEnd('/') + customUrl;
                        // else: bare string, treat as 'no override'
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
