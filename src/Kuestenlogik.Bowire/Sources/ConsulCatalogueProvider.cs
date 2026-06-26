// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Sources;

/// <summary>
/// Queries a HashiCorp Consul agent's v1 catalogue API (#136 Phase
/// C) and materialises each registered service into a
/// <see cref="BowireCatalogueEntry"/>. Bound to
/// <c>Bowire:Discovery:Catalogue:Consul</c> via
/// <see cref="BowireConsulCatalogueOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two API calls per refresh:
/// </para>
/// <list type="number">
///   <item>
///     <c>GET /v1/catalog/services</c> — returns a map of
///     <c>{ "name": ["tag", ...] }</c>. We use the tag list to skip
///     services that don't match the optional
///     <see cref="BowireConsulCatalogueOptions.Tag"/> filter
///     up-front.
///   </item>
///   <item>
///     <c>GET /v1/catalog/service/{name}</c> for each matching
///     service. The response carries <c>ServiceAddress</c> (or the
///     fallback <c>Address</c>) + <c>ServicePort</c> which we
///     assemble into the entry URL using the configured
///     <see cref="BowireConsulCatalogueOptions.Scheme"/>.
///   </item>
/// </list>
/// <para>
/// Consul's catalogue carries only network coordinates — it
/// doesn't tell us which Bowire protocol plugin (rest / grpc /
/// signalr / ...) each service speaks. We surface the Consul tags
/// as the entry's tags so operators can filter in the workbench;
/// the protocol probe fan-out handles plugin selection at
/// invocation time, same as for hand-entered URLs.
/// </para>
/// <para>
/// One <c>HttpClient</c> per fetch — the call volume is tiny (one
/// catalogue refresh every few minutes) and the per-fetch instance
/// keeps the timeout / handler resets simple. Hosts with much
/// larger registries should run a pre-aggregation relay and point
/// Bowire at it via the cheaper <c>http</c> provider.
/// </para>
/// </remarks>
public sealed class ConsulCatalogueProvider : IBowireCatalogueProvider
{
    private readonly Func<BowireConsulCatalogueOptions> _optionsResolver;
    private readonly Func<HttpClient> _clientFactory;

    /// <summary>
    /// Parameterless ctor for the assembly-scan discovery path. Uses
    /// environment-variable-driven defaults so the registry can
    /// instantiate the provider before <c>AddBowireCatalogue</c>
    /// has wired explicit options.
    /// </summary>
    public ConsulCatalogueProvider() : this(
        () => ResolveDefaultOptions(),
        () => new HttpClient())
    { }

    /// <summary>
    /// Test seam — pass an explicit options resolver and HTTP-client
    /// factory. Production code uses the parameterless ctor; tests
    /// inject a <see cref="HttpClient"/> wrapping a mock handler so
    /// they don't make real network calls.
    /// </summary>
    internal ConsulCatalogueProvider(
        Func<BowireConsulCatalogueOptions> optionsResolver,
        Func<HttpClient> clientFactory)
    {
        _optionsResolver = optionsResolver;
        _clientFactory = clientFactory;
    }

    /// <inheritdoc/>
    public string Id => "consul";

    /// <inheritdoc/>
    public string Name => "Consul";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BowireCatalogueEntry>> FetchAsync(CancellationToken cancellationToken)
    {
        var options = _optionsResolver();
        var address = string.IsNullOrWhiteSpace(options.Address)
            ? "http://localhost:8500"
            : options.Address!.TrimEnd('/');
        var scheme = string.IsNullOrWhiteSpace(options.Scheme) ? "http" : options.Scheme!;
        var dcSuffix = string.IsNullOrWhiteSpace(options.Datacenter)
            ? string.Empty
            : $"?dc={Uri.EscapeDataString(options.Datacenter!)}";

        using var client = _clientFactory();
        client.Timeout = options.Timeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(10)
            : options.Timeout;
        if (!string.IsNullOrWhiteSpace(options.Token))
        {
            // Consul ACL token header. Per the v1 API docs this is
            // distinct from Authorization — Consul also accepts the
            // X-Consul-Token form for tokens issued via the legacy
            // ACL system; both forms map to the same gate on the
            // agent. We send X-Consul-Token because it's the one
            // that works regardless of agent version.
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Consul-Token", options.Token);
        }

        // Step 1 — fetch the service-name → tags map.
        var listUrl = new Uri($"{address}/v1/catalog/services{dcSuffix}");
        Dictionary<string, List<string>>? services;
        using (var listResponse = await client.GetAsync(listUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            listResponse.EnsureSuccessStatusCode();
            services = await listResponse.Content.ReadFromJsonAsync<Dictionary<string, List<string>>>(
                LocalCatalogueProvider.JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }

        if (services is null || services.Count == 0)
        {
            return Array.Empty<BowireCatalogueEntry>();
        }

        var entries = new List<BowireCatalogueEntry>();

        // Step 2 — per-service detail fetches. Consul exposes the
        // `consul` meta-service in every catalogue; skip it because
        // it isn't an application service operators want to invoke.
        foreach (var (name, tags) in services)
        {
            if (string.Equals(name, "consul", StringComparison.OrdinalIgnoreCase)) continue;

            // Optional tag filter — applied here on the cheap tag
            // list to avoid the per-service detail fetch for
            // services we wouldn't surface anyway.
            if (!string.IsNullOrWhiteSpace(options.Tag))
            {
                if (tags is null || !tags.Contains(options.Tag, StringComparer.OrdinalIgnoreCase))
                    continue;
            }

            var detailUrl = new Uri($"{address}/v1/catalog/service/{Uri.EscapeDataString(name)}{dcSuffix}");
            List<ConsulServiceInstance>? detail;
            try
            {
                using var detailResponse = await client.GetAsync(detailUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                detailResponse.EnsureSuccessStatusCode();
                detail = await detailResponse.Content.ReadFromJsonAsync<List<ConsulServiceInstance>>(
                    LocalCatalogueProvider.JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }
            // Per-service fetches are best-effort; one bad service
            // detail (deregistered between the list and detail call,
            // ACL-gated entry, ...) shouldn't tank the whole refresh.
            // Cancellation still propagates.
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
                continue;
            }

            if (detail is null) continue;

            foreach (var inst in detail)
            {
                // ServiceAddress is the address the service registered
                // for itself; falls back to Address (the node IP) when
                // the service didn't override it. ServicePort is
                // required for the URL to be useful.
                var host = !string.IsNullOrWhiteSpace(inst.ServiceAddress)
                    ? inst.ServiceAddress
                    : inst.Address;
                if (string.IsNullOrWhiteSpace(host)) continue;
                if (inst.ServicePort <= 0) continue;

                var url = $"{scheme}://{host}:{inst.ServicePort}";
                // Promote Consul tags to entry tags verbatim — the
                // workbench's filter popup keys on them.
                var entryTags = inst.ServiceTags is { Count: > 0 }
                    ? inst.ServiceTags
                    : tags;

                entries.Add(new BowireCatalogueEntry(
                    Url: url,
                    Name: name,
                    Tags: entryTags));
            }
        }

        return entries;
    }

    /// <summary>
    /// Resolve the default options from environment variables — the
    /// Consul address from <c>CONSUL_HTTP_ADDR</c> (the well-known
    /// Consul env-var) and the ACL token from
    /// <c>CONSUL_HTTP_TOKEN</c>. Both are optional; an unconfigured
    /// host falls back to <c>http://localhost:8500</c>.
    /// </summary>
    internal static BowireConsulCatalogueOptions ResolveDefaultOptions()
    {
        return new BowireConsulCatalogueOptions
        {
            Address = Environment.GetEnvironmentVariable("CONSUL_HTTP_ADDR"),
            Token = Environment.GetEnvironmentVariable("CONSUL_HTTP_TOKEN"),
        };
    }

    /// <summary>
    /// Subset of the Consul v1 catalogue detail response we care
    /// about. PascalCase property names match the Consul API; the
    /// shared <see cref="LocalCatalogueProvider.JsonOptions"/> reader
    /// uses case-insensitive matching, so the wire shape
    /// <c>"ServiceAddress"</c> resolves correctly.
    /// </summary>
    private sealed record ConsulServiceInstance(
        [property: JsonPropertyName("Address")] string? Address,
        [property: JsonPropertyName("ServiceAddress")] string? ServiceAddress,
        [property: JsonPropertyName("ServicePort")] int ServicePort,
        [property: JsonPropertyName("ServiceTags")] List<string>? ServiceTags);
}
