// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Integration coverage for <c>BowireCatalogueEndpoints</c> (#136 / #309).
/// Drives every public route of the catalogue-provider seam through a
/// TestServer: <c>/api/catalogue/info</c>, <c>/api/catalogue/entries</c>,
/// <c>/api/catalogue/refresh</c>, and the persisted-override CRUD trio
/// at <c>/api/catalogue/config</c>. Each test owns its own host so the
/// in-memory <see cref="BowireCatalogueProviderAccessor"/> and override
/// store can be stubbed independently.
/// </summary>
public sealed class BowireCatalogueEndpointTests : IDisposable
{
    private readonly string? _previousEnv;
    private readonly string _overridePath;

    public BowireCatalogueEndpointTests()
    {
        // Redirect the override-config file into a per-test temp path so
        // tests don't fight over ~/.bowire/catalogue-config.json. The
        // env-var hook is the public test seam BowireCatalogueOverrideStore
        // documents.
        _previousEnv = Environment.GetEnvironmentVariable("BOWIRE_CATALOGUE_CONFIG_PATH");
        _overridePath = Path.Combine(
            Path.GetTempPath(),
            $"bowire-catalogue-test-{Guid.NewGuid():N}.json");
        Environment.SetEnvironmentVariable("BOWIRE_CATALOGUE_CONFIG_PATH", _overridePath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BOWIRE_CATALOGUE_CONFIG_PATH", _previousEnv);
        try { if (File.Exists(_overridePath)) File.Delete(_overridePath); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ----- GET /api/catalogue/info -----------------------------------

    [Fact]
    public async Task Info_returns_available_false_when_no_accessor_registered()
    {
        using var host = await BuildBareHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/catalogue/info", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("editable", doc.RootElement.GetProperty("visibility").GetString());
        Assert.False(doc.RootElement.GetProperty("hasOverride").GetBoolean());
    }

    [Fact]
    public async Task Info_reports_active_provider_and_options_visibility()
    {
        var provider = new StubProvider("stub", "Stub Provider");
        using var host = await BuildHostWithAccessor(
            new BowireCatalogueProviderAccessor(provider),
            options: o =>
            {
                o.Visibility = BowireCatalogueVisibility.Readonly;
                o.RefreshInterval = TimeSpan.FromSeconds(60);
            });
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/catalogue/info", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("stub", doc.RootElement.GetProperty("providerId").GetString());
        Assert.Equal("Stub Provider", doc.RootElement.GetProperty("providerName").GetString());
        Assert.Equal("readonly", doc.RootElement.GetProperty("visibility").GetString());
        Assert.Equal(60, doc.RootElement.GetProperty("refreshIntervalSeconds").GetInt32());
    }

    [Fact]
    public async Task Info_maps_hidden_visibility_to_wire_value()
    {
        using var host = await BuildHostWithAccessor(
            new BowireCatalogueProviderAccessor(null),
            options: o => o.Visibility = BowireCatalogueVisibility.Hidden);
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/catalogue/info", UriKind.Relative),
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("hidden", doc.RootElement.GetProperty("visibility").GetString());
    }

    // ----- GET /api/catalogue/entries --------------------------------

    [Fact]
    public async Task Entries_returns_empty_list_when_no_provider()
    {
        using var host = await BuildBareHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/catalogue/entries", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        // JsonOptions strips nulls — providerId is absent rather than
        // serialised as null. The contract is "entries is the empty
        // array" — that's the symmetry the workbench relies on.
        Assert.False(doc.RootElement.TryGetProperty("providerId", out _));
        Assert.Empty(doc.RootElement.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task Entries_returns_provider_snapshot()
    {
        var provider = new StubProvider("stub", "Stub Provider",
            new BowireCatalogueEntry(Url: "https://a.example.com", Name: "A"),
            new BowireCatalogueEntry(Url: "https://b.example.com", Name: "B"));
        using var host = await BuildHostWithAccessor(
            new BowireCatalogueProviderAccessor(provider));
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/catalogue/entries", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("stub", doc.RootElement.GetProperty("providerId").GetString());
        Assert.Equal("Stub Provider", doc.RootElement.GetProperty("providerName").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Entries_returns_502_problem_details_when_provider_throws()
    {
        var provider = new ThrowingProvider("boom",
            new InvalidOperationException("upstream went away"));
        using var host = await BuildHostWithAccessor(
            new BowireCatalogueProviderAccessor(provider));
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/catalogue/entries", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("urn:bowire:catalogue:fetch-failed",
            doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(502, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("boom", doc.RootElement.GetProperty("providerId").GetString());
    }

    // ----- POST /api/catalogue/refresh -------------------------------

    [Fact]
    public async Task Refresh_returns_provider_snapshot()
    {
        var provider = new StubProvider("stub", "Stub",
            new BowireCatalogueEntry(Url: "https://x.example.com", Name: "X"));
        using var host = await BuildHostWithAccessor(
            new BowireCatalogueProviderAccessor(provider));
        var client = host.GetTestClient();

        using var refreshBody = new StringContent(string.Empty);
        using var resp = await client.PostAsync(
            new Uri("/api/catalogue/refresh", UriKind.Relative),
            refreshBody,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("stub", doc.RootElement.GetProperty("providerId").GetString());
        Assert.Single(doc.RootElement.GetProperty("entries").EnumerateArray());
    }

    // ----- GET /api/catalogue/config ---------------------------------

    [Fact]
    public async Task Config_get_returns_hasOverride_false_when_no_store()
    {
        // BuildHostWithAccessor wires accessor + (optional) options; the
        // override store must be added explicitly via BuildHostWithStore.
        using var host = await BuildBareHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/catalogue/config", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("hasOverride").GetBoolean());
    }

    [Fact]
    public async Task Config_get_returns_persisted_payload_with_secrets_masked()
    {
        using var host = await BuildHostWithStore();
        var client = host.GetTestClient();
        var store = host.Services.GetRequiredService<BowireCatalogueOverrideStore>();
        store.Save(new BowireCatalogueOverride
        {
            Provider = "http",
            Http = new BowireHttpCatalogueOptions
            {
                Url = "https://catalogue.example.com",
                Authorization = "Bearer real-secret",
            },
        });

        using var resp = await client.GetAsync(
            new Uri("/api/catalogue/config", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("hasOverride").GetBoolean());
        Assert.Equal("http", doc.RootElement.GetProperty("provider").GetString());
        // The Authorization secret is replaced with the "__set__" sentinel
        // so the wire never carries a real bearer to the workbench.
        Assert.Equal("__set__",
            doc.RootElement.GetProperty("http").GetProperty("authorization").GetString());
    }

    [Fact]
    public async Task Config_get_masks_consul_token()
    {
        using var host = await BuildHostWithStore();
        var client = host.GetTestClient();
        var store = host.Services.GetRequiredService<BowireCatalogueOverrideStore>();
        store.Save(new BowireCatalogueOverride
        {
            Provider = "consul",
            Consul = new BowireConsulCatalogueOptions
            {
                Address = "http://consul.local:8500",
                Token = "secret-consul-token",
                Datacenter = "dc1",
            },
        });

        using var resp = await client.GetAsync(
            new Uri("/api/catalogue/config", UriKind.Relative),
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("__set__",
            doc.RootElement.GetProperty("consul").GetProperty("token").GetString());
        Assert.Equal("dc1",
            doc.RootElement.GetProperty("consul").GetProperty("datacenter").GetString());
    }

    // ----- POST /api/catalogue/config --------------------------------

    [Fact]
    public async Task Config_post_returns_404_when_no_store()
    {
        using var host = await BuildBareHost();
        var client = host.GetTestClient();

        using var body = JsonContent.Create(new { provider = "local" });
        using var resp = await client.PostAsync(
            new Uri("/api/catalogue/config", UriKind.Relative),
            body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var responseBody = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("urn:bowire:catalogue:no-store", responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_post_with_bad_json_returns_400()
    {
        using var host = await BuildHostWithStore();
        var client = host.GetTestClient();

        using var body = new StringContent("{ not json", Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(
            new Uri("/api/catalogue/config", UriKind.Relative),
            body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var responseBody = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("urn:bowire:catalogue:bad-config", responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_post_with_empty_body_returns_400_missing_body()
    {
        using var host = await BuildHostWithStore();
        var client = host.GetTestClient();

        // "null" deserialises to a null BowireCatalogueOverride, hitting
        // the explicit "Missing body" branch (distinct from the parse
        // failure path).
        using var body = new StringContent("null", Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(
            new Uri("/api/catalogue/config", UriKind.Relative),
            body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var responseBody = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Missing body", responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_post_persists_override_and_hot_swaps_provider()
    {
        using var host = await BuildHostWithStore();
        var client = host.GetTestClient();

        using var body = JsonContent.Create(new
        {
            provider = "local",
            local = new { path = Path.GetTempPath() }
        });
        using var resp = await client.PostAsync(
            new Uri("/api/catalogue/config", UriKind.Relative),
            body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var responseBody = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(responseBody);
        Assert.True(doc.RootElement.GetProperty("hasOverride").GetBoolean());
        Assert.Equal("local", doc.RootElement.GetProperty("providerId").GetString());

        // The persisted file lives under the env-redirected path so
        // we can read it back directly.
        Assert.True(File.Exists(_overridePath));
    }

    [Fact]
    public async Task Config_post_keep_sentinel_preserves_existing_secret()
    {
        using var host = await BuildHostWithStore();
        var client = host.GetTestClient();
        var store = host.Services.GetRequiredService<BowireCatalogueOverrideStore>();
        store.Save(new BowireCatalogueOverride
        {
            Provider = "http",
            Http = new BowireHttpCatalogueOptions
            {
                Url = "https://catalogue.example.com",
                Authorization = "Bearer original-secret",
            },
        });

        // The UI sends "__keep__" to mean "don't touch the stored token".
        using var body = JsonContent.Create(new
        {
            provider = "http",
            http = new
            {
                url = "https://catalogue.example.com/v2",
                authorization = "__keep__",
            }
        });
        using var resp = await client.PostAsync(
            new Uri("/api/catalogue/config", UriKind.Relative),
            body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Reload via the store API to verify the persisted secret survived.
        Assert.Equal("Bearer original-secret", store.Current?.Http?.Authorization);
        Assert.Equal("https://catalogue.example.com/v2", store.Current?.Http?.Url);
    }

    [Fact]
    public async Task Config_post_clear_sentinel_wipes_existing_secret()
    {
        using var host = await BuildHostWithStore();
        var client = host.GetTestClient();
        var store = host.Services.GetRequiredService<BowireCatalogueOverrideStore>();
        store.Save(new BowireCatalogueOverride
        {
            Provider = "consul",
            Consul = new BowireConsulCatalogueOptions
            {
                Address = "http://consul.local:8500",
                Token = "old-token",
            },
        });

        using var body = JsonContent.Create(new
        {
            provider = "consul",
            consul = new
            {
                address = "http://consul.local:8500",
                token = "__clear__",
            }
        });
        using var resp = await client.PostAsync(
            new Uri("/api/catalogue/config", UriKind.Relative),
            body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Null(store.Current?.Consul?.Token);
    }

    // ----- DELETE /api/catalogue/config ------------------------------

    [Fact]
    public async Task Config_delete_returns_hasOverride_false_when_no_store()
    {
        using var host = await BuildBareHost();
        var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Delete,
            new Uri("/api/catalogue/config", UriKind.Relative));
        using var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("hasOverride").GetBoolean());
    }

    [Fact]
    public async Task Config_delete_clears_persisted_override()
    {
        using var host = await BuildHostWithStore();
        var client = host.GetTestClient();
        var store = host.Services.GetRequiredService<BowireCatalogueOverrideStore>();
        store.Save(new BowireCatalogueOverride { Provider = "local" });
        Assert.NotNull(store.Current);

        using var req = new HttpRequestMessage(HttpMethod.Delete,
            new Uri("/api/catalogue/config", UriKind.Relative));
        using var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("hasOverride").GetBoolean());
        Assert.Null(store.Current);
    }

    // ----- Host builders ---------------------------------------------

    private static async Task<IHost> BuildBareHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireCatalogueEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> BuildHostWithAccessor(
        BowireCatalogueProviderAccessor accessor,
        Action<BowireCatalogueOptions>? options = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireCatalogueEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       s.AddSingleton(accessor);
                       if (options is not null) s.Configure(options);
                       else s.AddOptions<BowireCatalogueOptions>();
                   });
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> BuildHostWithStore()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireCatalogueEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       var accessor = new BowireCatalogueProviderAccessor(null);
                       s.AddSingleton(accessor);
                       s.AddSingleton(new BowireCatalogueOverrideStore(accessor));
                   });
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    // ----- Stubs -----------------------------------------------------

    private sealed class StubProvider : IBowireCatalogueProvider
    {
        private readonly BowireCatalogueEntry[] _entries;
        public StubProvider(string id, string name, params BowireCatalogueEntry[] entries)
        {
            Id = id;
            Name = name;
            _entries = entries;
        }
        public string Id { get; }
        public string Name { get; }
        public Task<IReadOnlyList<BowireCatalogueEntry>> FetchAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<BowireCatalogueEntry>>(_entries);
    }

    private sealed class ThrowingProvider(string id, Exception ex) : IBowireCatalogueProvider
    {
        public string Id => id;
        public string Name => id;
        public Task<IReadOnlyList<BowireCatalogueEntry>> FetchAsync(CancellationToken cancellationToken)
            => Task.FromException<IReadOnlyList<BowireCatalogueEntry>>(ex);
    }
}
