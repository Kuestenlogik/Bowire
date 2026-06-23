// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.Rest;

/// <summary>
/// Bowire protocol plugin for REST APIs. Discovers endpoints by fetching an
/// OpenAPI 3 / Swagger document from the configured server URL, then exercises
/// them via <see cref="RestInvoker"/>. Auto-discovered by
/// <see cref="BowireProtocolRegistry"/>.
/// </summary>
public sealed class BowireRestProtocol : IBowireProtocol, IInlineHttpInvoker, IDisposable
{
    // One HttpClient for the lifetime of the plugin — fine for a dev tool.
    // 30s timeout matches the OAuth proxy timeout used elsewhere in Bowire.
    // Built lazily from BowireHttpClientFactory in Initialize() so the
    // localhost-cert opt-in (Bowire:TrustLocalhostCert) reaches the
    // certificate validation callback. Falls back to a vanilla HttpClient
    // for test paths that skip Initialize.
    private HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Cache parsed OpenAPI docs per server URL so InvokeAsync doesn't re-fetch.
    // Key: serverUrl. Value: discovered services + lookup index.
    private readonly ConcurrentDictionary<string, RestSchemaCache> _cache = new(StringComparer.Ordinal);

    // Captured during Initialize() in embedded mode so DiscoverAsync can read
    // the host's API descriptions directly instead of fetching an OpenAPI doc
    // over HTTP.
    private IServiceProvider? _serviceProvider;

    public string Name => "REST";
    public string Description => "OpenAPI / Swagger — discover + invoke HTTP services described by an OpenAPI document.";
    public string Id => "rest";

    public string IconSvg => """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="16" height="16"><path d="M8 3H6a2 2 0 00-2 2v4c0 1.1-.9 2-2 2 1.1 0 2 .9 2 2v4a2 2 0 002 2h2"/><path d="M16 3h2a2 2 0 012 2v4c0 1.1.9 2 2 2-1.1 0-2 .9-2 2v4a2 2 0 01-2 2h-2"/></svg>""";

    public void Initialize(IServiceProvider? serviceProvider)
    {
        _serviceProvider = serviceProvider;
        var config = serviceProvider?.GetService<IConfiguration>();
        _http = BowireHttpClientFactory.Create(config, Id, TimeSpan.FromSeconds(30));
    }

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        // Uploaded OpenAPI documents (via /api/openapi/upload) take precedence
        // when present so users can override or augment runtime discovery with
        // a local schema file. Multiple uploads are merged.
        var uploadedServices = await DiscoverFromUploadsAsync(ct).ConfigureAwait(false);

        // Embedded discovery — when we have a service provider with API
        // descriptions, that's the in-process source of truth (no HTTP).
        List<BowireServiceInfo> embeddedServices = [];
        if (EmbeddedDiscovery.TryDiscover(_serviceProvider, out var embedded))
        {
            CacheEmbeddedSchemas(embedded);
            embeddedServices = embedded;
        }

        // URL discovery — only fires when a serverUrl is supplied (standalone).
        var urlServices = !string.IsNullOrEmpty(serverUrl)
            ? await DiscoverInternalAsync(serverUrl, ct).ConfigureAwait(false)
            : [];

        // Merge in priority order: uploads > embedded > URL.
        if (uploadedServices.Count == 0 && embeddedServices.Count == 0)
            return urlServices;

        var merged = new List<BowireServiceInfo>(uploadedServices);
        merged.AddRange(embeddedServices);
        // Avoid duplicating services that came in via the URL when an upload or
        // embedded source already covered the same tag — uploads/embedded win.
        var taken = new HashSet<string>(merged.ConvertAll(s => s.Name), StringComparer.Ordinal);
        foreach (var svc in urlServices)
        {
            if (taken.Add(svc.Name)) merged.Add(svc);
        }
        return merged;
    }

    private async Task<List<BowireServiceInfo>> DiscoverFromUploadsAsync(CancellationToken ct)
    {
        var docs = OpenApiUploadStore.GetAll();
        if (docs.Count == 0) return [];
        var adapter = BowireOpenApiAdapterRegistry.TryGet();
        if (adapter is null) return [];

        var all = new List<BowireServiceInfo>();
        foreach (var doc in docs)
        {
            var parsed = await adapter.ParseAndDiscoverAsync(doc.Content, doc.SourceName, ct).ConfigureAwait(false);
            if (parsed is null) continue;

            var apiBaseUrl = parsed.ApiBaseUrl ?? string.Empty;
            var services = parsed.Services;
            foreach (var svc in services)
            {
                svc.OriginUrl = doc.SourceName;
                svc.IsUploaded = true;
            }

            // Index for invocation lookup, keyed by source name
            var index = services
                .SelectMany(svc => svc.Methods.Select(m => (key: svc.Name + "::" + m.Name, m)))
                .ToDictionary(pair => pair.key, pair => pair.m, StringComparer.Ordinal);
            _cache[doc.SourceName] = new RestSchemaCache(services, index, apiBaseUrl);

            all.AddRange(services);
        }
        return all;
    }

    private void CacheEmbeddedSchemas(List<BowireServiceInfo> services)
    {
        var index = services
            .SelectMany(svc => svc.Methods.Select(m => (key: svc.Name + "::" + m.Name, m)))
            .ToDictionary(pair => pair.key, pair => pair.m, StringComparer.Ordinal);
        // Empty key marks "embedded mode" — invocation falls through to whatever
        // serverUrl the BowireApiEndpoints layer resolves from the request.
        _cache[string.Empty] = new RestSchemaCache(services, index, string.Empty);
    }

    private async Task<List<BowireServiceInfo>> DiscoverInternalAsync(string docUrl, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(docUrl)) return [];
        var adapter = BowireOpenApiAdapterRegistry.TryGet();
        if (adapter is null) return [];

        var discovered = await adapter.FetchAndDiscoverAsync(docUrl, _http, ct).ConfigureAwait(false);
        if (discovered is null)
        {
            // The URL is not an OpenAPI document — drop any cache entry and let
            // other protocol plugins try this URL.
            _cache.TryRemove(docUrl, out _);
            return [];
        }

        // Compute the actual API base URL — preferred source is OpenAPI's
        // servers[0]. When that's missing or relative, fall back to the doc
        // URL's origin (scheme + host + port).
        var apiBaseUrl = ResolveApiBaseUrl(docUrl, discovered.ApiBaseUrl);

        var services = discovered.Services;

        // Tag each service with its origin doc URL so the multi-URL future
        // (and the embedded discovery path) can route invocations correctly.
        foreach (var svc in services)
        {
            svc.OriginUrl = docUrl;
        }

        // Build a lookup index so InvokeAsync can find a method by (service, method)
        var index = services
            .SelectMany(svc => svc.Methods.Select(m => (key: svc.Name + "::" + m.Name, m)))
            .ToDictionary(pair => pair.key, pair => pair.m, StringComparer.Ordinal);

        _cache[docUrl] = new RestSchemaCache(services, index, apiBaseUrl);

        return services;
    }

    /// <summary>
    /// Determines the URL Bowire will fire HTTP requests at when invoking a
    /// REST method. Priority:
    /// 1. <c>servers[0].url</c> from the OpenAPI document (most reliable;
    ///    pre-resolved by the adapter, passed in as <paramref name="fromSpec"/>)
    /// 2. The origin (scheme://host:port) of the document URL itself
    /// </summary>
    private static string ResolveApiBaseUrl(string docUrl, string? fromSpec)
    {
        if (!string.IsNullOrEmpty(fromSpec))
        {
            if (Uri.IsWellFormedUriString(fromSpec, UriKind.Absolute))
                return fromSpec.TrimEnd('/');

            // Relative server URL — resolve against the doc URL
            if (Uri.TryCreate(docUrl, UriKind.Absolute, out var docUri)
                && Uri.TryCreate(docUri, fromSpec, out var combined))
            {
                return combined.ToString().TrimEnd('/');
            }
        }

        // No usable servers entry — strip the path off the doc URL and use the origin
        if (Uri.TryCreate(docUrl, UriKind.Absolute, out var fallbackUri))
        {
            return $"{fallbackUri.Scheme}://{fallbackUri.Authority}";
        }
        return docUrl;
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        // #256 — Ad-hoc REST routing. When the operator drops a freeform
        // request without a discovered OpenAPI document, the freeform
        // builder sends an empty service + an HTTP verb (GET / POST /
        // …) as the 'method'. Detect that shape and skip the schema-
        // index lookup entirely; RestInvoker.InvokeAdHocAsync fires
        // a plain HttpClient call against the supplied serverUrl.
        // gRPC / GraphQL / other protocols' freeform flows still need
        // a service+method pair, so the convention is REST-specific.
        if (string.IsNullOrEmpty(service) && IsAdHocVerb(method))
        {
            var body = jsonMessages.Count > 0 ? jsonMessages[0] : null;
            return await RestInvoker.InvokeAdHocAsync(_http, serverUrl, method, body, metadata, ct)
                .ConfigureAwait(false);
        }

        // Embedded path: when running in-process, prime the embedded cache on
        // the fly if Discover hasn't been called yet. Lookup by (service, method)
        // hits the empty-key cache that EmbeddedDiscovery populates.
        if (!_cache.ContainsKey(string.Empty)
            && EmbeddedDiscovery.TryDiscover(_serviceProvider, out var embeddedServices))
        {
            CacheEmbeddedSchemas(embeddedServices);
        }

        if (_cache.TryGetValue(string.Empty, out var embeddedCache)
            && embeddedCache.Index.TryGetValue(service + "::" + method, out var embeddedMethod))
        {
            return await RestInvoker.InvokeAsync(_http, serverUrl, embeddedMethod, jsonMessages, metadata, ct)
                .ConfigureAwait(false);
        }

        if (!_cache.TryGetValue(serverUrl, out var cache))
        {
            // Cold cache — discover lazily so the first call after a server URL
            // change still works without forcing the user to refresh services first.
            await DiscoverInternalAsync(serverUrl, ct).ConfigureAwait(false);
            _cache.TryGetValue(serverUrl, out cache);
        }

        if (cache is null)
        {
            return new InvokeResult(
                Response: null,
                DurationMs: 0,
                Status: "Error",
                Metadata: new Dictionary<string, string>
                {
                    ["error"] = "No OpenAPI document found at " + serverUrl
                });
        }

        if (!cache.Index.TryGetValue(service + "::" + method, out var methodInfo))
        {
            return new InvokeResult(
                Response: null,
                DurationMs: 0,
                Status: "Error",
                Metadata: new Dictionary<string, string>
                {
                    ["error"] = "Unknown REST method: " + service + "/" + method
                });
        }

        // Use the OpenAPI servers[0] base URL when present — that's the actual
        // API endpoint, even if the OpenAPI doc was hosted somewhere else.
        var effectiveBase = cache.ApiBaseUrl ?? serverUrl;
        return await RestInvoker.InvokeAsync(_http, effectiveBase, methodInfo, jsonMessages, metadata, ct)
            .ConfigureAwait(false);
    }

    public IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        => AsyncEnumerable.Empty<string>();

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => Task.FromResult<IBowireChannel?>(null);

    /// <summary>
    /// Dispose the lazily-built <see cref="HttpClient"/>. The registry that
    /// owns the plugin instance disposes the plugin at host shutdown — same
    /// pattern as other long-lived HTTP-based protocol plugins.
    /// </summary>
    public void Dispose()
    {
        _http.Dispose();
    }

    /// <summary>
    /// <see cref="IInlineHttpInvoker"/> implementation. Lets the BowireApi
    /// /api/invoke endpoint dispatch a generic <see cref="BowireMethodInfo"/>
    /// over HTTP — used by the gRPC plugin's transcoded methods so the same
    /// call can be tested via gRPC OR via HTTP. The method info is supplied
    /// inline by the JS layer; no cache lookup happens.
    /// </summary>
    public Task<InvokeResult> InvokeHttpAsync(
        string serverUrl,
        BowireMethodInfo methodInfo,
        List<string> jsonMessages,
        Dictionary<string, string>? metadata,
        CancellationToken ct = default)
    {
        return RestInvoker.InvokeAsync(_http, serverUrl, methodInfo, jsonMessages, metadata, ct);
    }

    /// <summary>
    /// True when <paramref name="token"/> looks like one of the seven
    /// standard HTTP verbs. Drives the schema-free ad-hoc routing
    /// branch in <see cref="InvokeAsync"/> (#256).
    /// </summary>
    private static bool IsAdHocVerb(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var t = token.Trim();
        return string.Equals(t, "GET", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "POST", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "PUT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "DELETE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "PATCH", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "HEAD", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "OPTIONS", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record RestSchemaCache(
    List<BowireServiceInfo> Services,
    Dictionary<string, BowireMethodInfo> Index,
    string ApiBaseUrl);
