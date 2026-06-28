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

    // Cache resolved OpenAPI doc URLs per origin, so the next DiscoverInternal
    // pass against the same origin hits the right URL on the first try
    // instead of replaying the 8-probe sweep. Key: origin (scheme://host:port).
    // Value: the well-known doc URL that won the probe.
    private readonly ConcurrentDictionary<string, string> _probeResolved = new(StringComparer.Ordinal);

    // Captured during Initialize() in embedded mode so DiscoverAsync can read
    // the host's API descriptions directly instead of fetching an OpenAPI doc
    // over HTTP.
    private IServiceProvider? _serviceProvider;

    // Well-known OpenAPI document paths, ordered most-common-first.
    // Probed only when the supplied URL doesn't itself look like a spec URL
    // AND the initial fetch returned non-OpenAPI content. See
    // ProbeWellKnownPathsAsync.
    private static readonly string[] WellKnownOpenApiPaths =
    [
        "/openapi.json",          // .NET 10 minimal-API, Springdoc default
        "/openapi/v1.json",       // .NET 10 native AddOpenApi / MapOpenApi
        "/swagger/v1/swagger.json", // Swashbuckle ASP.NET default
        "/swagger.json",          // older Swashbuckle / Swagger UI default
        "/v3/api-docs",           // Springdoc OpenAPI 3 default
        "/v3/api-docs.yaml",
        "/api-docs",              // older Springfox
        "/openapi.yaml",          // common YAML alternative
    ];

    // Per-probe timeout — short so the 8-probe sweep doesn't block discovery
    // when the origin is unreachable.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

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

        // Fast path: the operator already supplied a well-known path on a
        // previous call against this origin — fetch from the resolved URL
        // directly so we don't re-run the probe sweep.
        if (TryGetOrigin(docUrl, out var fastOrigin)
            && _probeResolved.TryGetValue(fastOrigin, out var cachedUrl)
            && !string.Equals(cachedUrl, docUrl, StringComparison.Ordinal))
        {
            var fromCache = await TryDiscoverAtAsync(adapter, cachedUrl, ct).ConfigureAwait(false);
            // CommitDiscovery uses cachedUrl as the cache key + base for
            // apiBaseUrl resolution, but the SERVICES' OriginUrl must
            // carry the operator-supplied docUrl so the workbench groups
            // them under the URL the operator actually typed — not the
            // well-known path the probe found. Operator feedback: 'wenn
            // ich http://localhost:5181 habe ich nach discover 2 einträge:
            // http://localhost:5181 → 0 services und
            // http://localhost:5181/openapi/v1.json → 5 services. das ist
            // merkwürdig, ich hätte die unter http://localhost:5181
            // erwartet und nur einen eintrag.'
            if (fromCache is not null)
            {
                var result = CommitDiscovery(cachedUrl, fromCache);
                RetagOriginUrl(result, docUrl);
                return result;
            }
            // Cached URL stopped responding — fall through to the regular path.
            _probeResolved.TryRemove(fastOrigin, out _);
        }

        var discovered = await adapter.FetchAndDiscoverAsync(docUrl, _http, ct).ConfigureAwait(false);
        if (discovered is null)
        {
            // The URL is not an OpenAPI document — drop any cache entry, then
            // try the well-known OpenAPI doc paths against the same origin
            // before giving up. Skips when the supplied URL itself already
            // looks like a spec URL (e.g. `/foo.json` that returned non-OpenAPI
            // — probing then is noise).
            _cache.TryRemove(docUrl, out _);

            if (!LooksLikeSpecUrl(docUrl) && TryGetOrigin(docUrl, out var origin))
            {
                var (probedUrl, probedResult) = await ProbeWellKnownPathsAsync(
                    adapter, origin, ct).ConfigureAwait(false);
                if (probedResult is not null)
                {
                    _probeResolved[origin] = probedUrl;
                    RestProbeLog.Info(
                        $"REST discovery resolved {origin} via well-known path {probedUrl}");
                    var probed = CommitDiscovery(probedUrl, probedResult);
                    RetagOriginUrl(probed, docUrl);
                    return probed;
                }

                RestProbeLog.Debug($"no OpenAPI document found at {origin}");
            }

            // Let other protocol plugins try this URL.
            return [];
        }

        return CommitDiscovery(docUrl, discovered);
    }

    /// <summary>
    /// Overwrite the <see cref="BowireServiceInfo.OriginUrl"/> on every
    /// service so the workbench groups them under the operator-supplied URL
    /// rather than the well-known spec path the probe resolved. CommitDiscovery
    /// always tags services with whatever doc URL it cached against; this
    /// helper retags the result for the auto-probe paths.
    /// </summary>
    private static void RetagOriginUrl(List<BowireServiceInfo> services, string originUrl)
    {
        foreach (var svc in services)
        {
            svc.OriginUrl = originUrl;
        }
    }

    /// <summary>
    /// Stamp a successful discovery into the schema cache, tag each service
    /// with its origin doc URL, and return the service list. Shared by the
    /// direct-fetch path and the probe-resolved path.
    /// </summary>
    private List<BowireServiceInfo> CommitDiscovery(string docUrl, BowireOpenApiDiscoveryResult discovered)
    {
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
    /// Sweep the well-known OpenAPI doc paths against <paramref name="origin"/>
    /// until one of them returns a parseable spec. Each probe gets its own
    /// short timeout so the sweep can't stall discovery on an unreachable
    /// host. Defensive — a 5xx or DNS failure on one probe doesn't kill the
    /// loop; that path just gets logged at debug-level.
    /// </summary>
    private async Task<(string ProbeUrl, BowireOpenApiDiscoveryResult? Result)> ProbeWellKnownPathsAsync(
        IBowireOpenApiAdapter adapter, string origin, CancellationToken ct)
    {
        foreach (var path in WellKnownOpenApiPaths)
        {
            var probeUrl = origin + path;
            var result = await TryDiscoverAtAsync(adapter, probeUrl, ct).ConfigureAwait(false);
            if (result is not null) return (probeUrl, result);
        }
        return (string.Empty, null);
    }

    /// <summary>
    /// One attempt at the adapter's FetchAndDiscoverAsync wrapped in a
    /// per-probe timeout + try/catch so a single misbehaving probe URL
    /// (timeout, DNS failure, 500…) can't take down the rest of the sweep.
    /// </summary>
    private async Task<BowireOpenApiDiscoveryResult?> TryDiscoverAtAsync(
        IBowireOpenApiAdapter adapter, string probeUrl, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);
        try
        {
            return await adapter.FetchAndDiscoverAsync(probeUrl, _http, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Per-probe timeout fired — try the next candidate.
            RestProbeLog.Debug($"probe timeout: {probeUrl}");
            return null;
        }
        catch (Exception ex)
        {
            // HttpRequestException (network unreachable, DNS, refused),
            // 5xx that the adapter surfaces as a throw, JSON / YAML parse
            // crash — record at debug level so the sweep keeps going.
            RestProbeLog.Debug($"probe failed: {probeUrl} ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="url"/> already looks like an OpenAPI / Swagger
    /// document URL — by path suffix (<c>.json / .yaml / .yml</c>) or by a
    /// substring marker (<c>swagger / openapi / api-docs</c>). Skips the
    /// probe sweep so a user-supplied <c>/foo.json</c> that came back as
    /// non-OpenAPI doesn't trigger 8 superfluous round-trips against the
    /// origin.
    /// </summary>
    private static bool LooksLikeSpecUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var path = uri.AbsolutePath;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Contains("swagger", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Contains("openapi", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Contains("api-docs", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Extract the scheme+authority origin from <paramref name="url"/>.
    /// Returns false when the input isn't a usable absolute URL — probing
    /// only makes sense when we have a real origin to swap paths against.
    /// </summary>
    private static bool TryGetOrigin(string url, out string origin)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            origin = $"{uri.Scheme}://{uri.Authority}";
            return true;
        }
        origin = string.Empty;
        return false;
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
