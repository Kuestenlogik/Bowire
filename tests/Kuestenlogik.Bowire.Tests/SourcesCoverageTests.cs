// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Sources;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Gap-closing coverage for the built-in catalogue providers — the
/// env-var default resolvers and the Consul best-effort / URL-assembly
/// edge branches the happy-path tests in
/// <see cref="BowireCatalogueProviderRegistryTests"/> don't reach.
/// Uses the provider test seams (explicit options + client factory), so
/// no real network or env mutation is required.
/// </summary>
public sealed class SourcesCoverageTests
{
    // ---------------- default-option / default-path resolvers ----------------

    [Fact]
    public void Local_ResolveDefaultPath_points_at_dot_bowire_when_no_env()
    {
        // With no BOWIRE_CATALOGUE_PATH override the resolver lands on
        // <user-profile>/.bowire/catalogue.json (or empty when the host
        // has no profile dir — both are non-throwing).
        var path = LocalCatalogueProvider.ResolveDefaultPath();
        Assert.True(path.Length == 0 || path.Replace('\\', '/').EndsWith(".bowire/catalogue.json", StringComparison.Ordinal),
            $"unexpected default path: '{path}'");
    }

    [Fact]
    public void Http_ResolveDefaultOptions_returns_options_object()
    {
        var options = HttpCatalogueProvider.ResolveDefaultOptions();
        Assert.NotNull(options);
    }

    [Fact]
    public void Consul_ResolveDefaultOptions_returns_options_object()
    {
        var options = ConsulCatalogueProvider.ResolveDefaultOptions();
        Assert.NotNull(options);
    }

    // ------------------------- Consul edge branches -------------------------

    private static StubHttpMessageHandler ConsulHandler(string servicesJson, string detailJson, HttpStatusCode detailStatus = HttpStatusCode.OK)
        => new((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/v1/catalog/services")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(servicesJson, Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(detailStatus)
            {
                Content = new StringContent(detailJson, Encoding.UTF8, "application/json"),
            };
        });

    [Fact]
    public async Task Consul_returns_empty_when_only_meta_service_registered()
    {
        // { "consul": [] } → the meta-service is skipped, nothing left.
        using var handler = ConsulHandler("""{ "consul": [] }""", "[]");
        var provider = new ConsulCatalogueProvider(
            () => new BowireConsulCatalogueOptions { Address = "http://consul.local:8500" },
            () => new HttpClient(handler, disposeHandler: false));

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task Consul_returns_empty_when_services_map_is_empty()
    {
        using var handler = ConsulHandler("{}", "[]");
        var provider = new ConsulCatalogueProvider(
            () => new BowireConsulCatalogueOptions { Address = "http://consul.local:8500" },
            () => new HttpClient(handler, disposeHandler: false));

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task Consul_detail_fetch_failure_is_best_effort_skipped()
    {
        // The list succeeds; the per-service detail returns 500. Best-effort
        // policy drops that service rather than throwing the whole refresh.
        using var handler = ConsulHandler("""{ "payments": ["bowire"] }""", "", HttpStatusCode.InternalServerError);
        var provider = new ConsulCatalogueProvider(
            () => new BowireConsulCatalogueOptions { Address = "http://consul.local:8500" },
            () => new HttpClient(handler, disposeHandler: false));

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task Consul_falls_back_to_node_address_and_skips_invalid_instances()
    {
        // Three instances: (1) blank ServiceAddress → falls back to Address,
        // (2) blank host entirely → skipped, (3) ServicePort 0 → skipped.
        var detail = JsonSerializer.Serialize(new object[]
        {
            new { Address = "10.9.9.9", ServiceAddress = "", ServicePort = 7000, ServiceTags = (string[]?)null },
            new { Address = "", ServiceAddress = "", ServicePort = 7001, ServiceTags = (string[]?)null },
            new { Address = "10.9.9.9", ServiceAddress = "10.1.1.1", ServicePort = 0, ServiceTags = (string[]?)null },
        });
        using var handler = ConsulHandler("""{ "svc": ["t"] }""", detail);
        var provider = new ConsulCatalogueProvider(
            () => new BowireConsulCatalogueOptions { Address = "http://consul.local:8500/", Scheme = "" },
            () => new HttpClient(handler, disposeHandler: false));

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);
        // Only the first instance survives; empty Scheme defaults to http;
        // ServiceAddress blank → node Address used.
        var entry = Assert.Single(entries);
        Assert.Equal("http://10.9.9.9:7000", entry.Url);
        // With no ServiceTags on the instance, the service-level tags apply.
        Assert.Contains("t", entry.Tags!);
    }

    [Fact]
    public async Task Consul_datacenter_option_appends_dc_query()
    {
        string? observedListQuery = null;
        using var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/v1/catalog/services")
            {
                observedListQuery = req.RequestUri.Query;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var provider = new ConsulCatalogueProvider(
            () => new BowireConsulCatalogueOptions { Address = "http://consul.local:8500", Datacenter = "dc1", Token = "s3cr3t" },
            () => new HttpClient(handler, disposeHandler: false));

        await provider.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Equal("?dc=dc1", observedListQuery);
    }
}
