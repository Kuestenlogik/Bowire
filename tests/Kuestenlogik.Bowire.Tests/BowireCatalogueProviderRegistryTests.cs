// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for <see cref="BowireCatalogueProviderRegistry"/> + the
/// three built-in providers shipping in core (#136 Phases A / B / C).
/// </summary>
public sealed class BowireCatalogueProviderRegistryTests
{
    // CA1861 — pre-computed expected arrays for assertions so the
    // analyzer doesn't flag repeated `new[] { ... }` allocations.
    private static readonly string[] ExpectedRestProtocols = ["rest"];
    private static readonly string[] ExpectedStagingTags = ["env:staging"];

    [Fact]
    public void Discover_Finds_Built_In_Providers()
    {
        var providers = BowireCatalogueProviderRegistry.Discover();

        Assert.Contains("local", providers.Keys);
        Assert.Contains("http", providers.Keys);
        Assert.Contains("consul", providers.Keys);
    }

    [Fact]
    public void Discover_Uses_Case_Insensitive_Keys()
    {
        var providers = BowireCatalogueProviderRegistry.Discover();
        Assert.True(providers.ContainsKey("LOCAL"));
        Assert.True(providers.ContainsKey("Http"));
    }

    [Fact]
    public void Resolve_Returns_Null_When_Provider_Is_Empty()
    {
        var resolved = BowireCatalogueProviderRegistry.Resolve(new BowireCatalogueOptions());
        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_Throws_When_Provider_Is_Unknown()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BowireCatalogueProviderRegistry.Resolve(new BowireCatalogueOptions { Provider = "nope" }));

        // Error message names the offending id and points the operator
        // at the install step — same pattern as the auth registry.
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Catalogue", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_Returns_Built_In_Provider_When_Selected()
    {
        var resolved = BowireCatalogueProviderRegistry.Resolve(new BowireCatalogueOptions { Provider = "local" });
        Assert.NotNull(resolved);
        Assert.Equal("local", resolved!.Id);
        Assert.IsType<LocalCatalogueProvider>(resolved);
    }

    internal static string[] RestProtocols => ExpectedRestProtocols;
    internal static string[] StagingTags => ExpectedStagingTags;
    internal static string[] RestProtocolsBowire => BowireTagsArray;

    private static readonly string[] BowireTagsArray = ["bowire"];
}

/// <summary>
/// Tests for <see cref="LocalCatalogueProvider"/> — the JSON-on-disk
/// provider.
/// </summary>
public sealed class LocalCatalogueProviderTests : IDisposable
{
    private readonly string _tempDir;

    public LocalCatalogueProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-catalogue-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task FetchAsync_Returns_Empty_When_File_Missing()
    {
        var path = Path.Combine(_tempDir, "missing.json");
        var provider = new LocalCatalogueProvider(() => path);

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task FetchAsync_Reads_Entries_From_File()
    {
        var path = Path.Combine(_tempDir, "catalogue.json");
        await File.WriteAllTextAsync(path, """
            {
              "version": 1,
              "entries": [
                { "url": "https://api.example.com", "name": "Example", "protocols": ["rest"], "tags": ["env:staging"] },
                { "url": "grpc://grpc.example.com:443" }
              ]
            }
            """, TestContext.Current.CancellationToken);
        var provider = new LocalCatalogueProvider(() => path);

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, entries.Count);
        Assert.Equal("https://api.example.com", entries[0].Url);
        Assert.Equal("Example", entries[0].Name);
        Assert.Equal(BowireCatalogueProviderRegistryTests.RestProtocols, entries[0].Protocols);
        Assert.Equal(BowireCatalogueProviderRegistryTests.StagingTags, entries[0].Tags);
    }

    [Fact]
    public async Task FetchAsync_Drops_Entries_Without_Url()
    {
        // Hand-edited file might land here — guard at the boundary.
        var path = Path.Combine(_tempDir, "catalogue.json");
        await File.WriteAllTextAsync(path, """
            {
              "entries": [
                { "url": "https://valid.example.com" },
                { "url": "", "name": "Empty URL" },
                { "name": "No URL field" }
              ]
            }
            """, TestContext.Current.CancellationToken);
        var provider = new LocalCatalogueProvider(() => path);

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Single(entries);
        Assert.Equal("https://valid.example.com", entries[0].Url);
    }

    [Fact]
    public void Id_And_Name_Match_Documented_Wire_Surface()
    {
        var provider = new LocalCatalogueProvider();
        Assert.Equal("local", provider.Id);
        Assert.Equal("Local file", provider.Name);
    }
}

/// <summary>
/// Tests for <see cref="HttpCatalogueProvider"/> — exercise the
/// fetch path with a mock <see cref="HttpMessageHandler"/>.
/// </summary>
public sealed class HttpCatalogueProviderTests
{
    [Fact]
    public async Task FetchAsync_Returns_Empty_When_Url_Is_Null()
    {
        using var client = new HttpClient();
        var provider = new HttpCatalogueProvider(
            () => new BowireHttpCatalogueOptions { Url = null },
            () => client);

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task FetchAsync_Parses_Remote_Document()
    {
        using var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(new Uri("https://catalogue.example.com/list"), req.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "version": 1,
                      "entries": [
                        { "url": "https://payments.example.com", "name": "Payments" }
                      ]
                    }
                    """, Encoding.UTF8, "application/json"),
            };
        });

        var provider = new HttpCatalogueProvider(
            () => new BowireHttpCatalogueOptions { Url = "https://catalogue.example.com/list" },
            () => new HttpClient(handler, disposeHandler: false));

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Single(entries);
        Assert.Equal("https://payments.example.com", entries[0].Url);
        Assert.Equal("Payments", entries[0].Name);
    }

    [Fact]
    public async Task FetchAsync_Sends_Authorization_Header()
    {
        string? observedAuth = null;
        using var handler = new StubHttpMessageHandler((req, _) =>
        {
            observedAuth = req.Headers.TryGetValues("Authorization", out var v) ? string.Join(",", v) : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"entries": []}""", Encoding.UTF8, "application/json"),
            };
        });

        var provider = new HttpCatalogueProvider(
            () => new BowireHttpCatalogueOptions
            {
                Url = "https://catalogue.example.com/list",
                Authorization = "Bearer xyz123",
            },
            () => new HttpClient(handler, disposeHandler: false));

        await provider.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Bearer xyz123", observedAuth);
    }

    [Fact]
    public async Task FetchAsync_Throws_On_Non_Success_Response()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var provider = new HttpCatalogueProvider(
            () => new BowireHttpCatalogueOptions { Url = "https://catalogue.example.com/list" },
            () => new HttpClient(handler, disposeHandler: false));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.FetchAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Id_And_Name_Match_Documented_Wire_Surface()
    {
        var provider = new HttpCatalogueProvider();
        Assert.Equal("http", provider.Id);
        Assert.Equal("HTTP endpoint", provider.Name);
    }
}

/// <summary>
/// Tests for <see cref="ConsulCatalogueProvider"/> — exercise the
/// two-step catalogue list / per-service detail flow.
/// </summary>
public sealed class ConsulCatalogueProviderTests
{
    [Fact]
    public async Task FetchAsync_Materialises_Entries_From_Catalog_Api()
    {
        using var handler = new StubHttpMessageHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/v1/catalog/services")
            {
                // Consul's listing endpoint returns { name: [tags] }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {
                          "consul": [],
                          "payments": ["bowire", "v2"],
                          "orders": ["bowire"]
                        }
                        """, Encoding.UTF8, "application/json"),
                };
            }
            if (path.StartsWith("/v1/catalog/service/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new[]
                    {
                        new
                        {
                            Address = "10.0.0.1",
                            ServiceAddress = "10.1.2.3",
                            ServicePort = 8080,
                            ServiceTags = BowireCatalogueProviderRegistryTests.RestProtocolsBowire,
                        }
                    }), Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new ConsulCatalogueProvider(
            () => new BowireConsulCatalogueOptions
            {
                Address = "http://consul.local:8500",
                Scheme = "https",
            },
            () => new HttpClient(handler, disposeHandler: false));

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);

        // Two app services (consul meta-service skipped).
        Assert.Equal(2, entries.Count);
        // Entry URL uses the configured scheme + the registered
        // ServiceAddress over the Node Address.
        Assert.Contains(entries, e => e.Url == "https://10.1.2.3:8080");
        Assert.All(entries, e => Assert.Contains("bowire", e.Tags!));
    }

    [Fact]
    public async Task FetchAsync_Honours_Tag_Filter()
    {
        using var handler = new StubHttpMessageHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/v1/catalog/services")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {
                          "in-scope": ["bowire"],
                          "out-of-scope": ["other"]
                        }
                        """, Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Address = "10.0.0.1",
                        ServiceAddress = "10.0.0.1",
                        ServicePort = 1234,
                        ServiceTags = BowireCatalogueProviderRegistryTests.RestProtocolsBowire,
                    }
                }), Encoding.UTF8, "application/json"),
            };
        });

        var provider = new ConsulCatalogueProvider(
            () => new BowireConsulCatalogueOptions
            {
                Address = "http://consul.local:8500",
                Tag = "bowire",
            },
            () => new HttpClient(handler, disposeHandler: false));

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);
        // Only the in-scope service surfaces.
        Assert.Single(entries);
        Assert.Equal("in-scope", entries[0].Name);
    }

    [Fact]
    public void Id_And_Name_Match_Documented_Wire_Surface()
    {
        var provider = new ConsulCatalogueProvider();
        Assert.Equal("consul", provider.Id);
        Assert.Equal("Consul", provider.Name);
    }
}

/// <summary>
/// Tests for the DI seam in
/// <see cref="BowireCatalogueServiceCollectionExtensions"/>.
/// </summary>
public sealed class BowireCatalogueServiceCollectionExtensionsTests
{
    [Fact]
    public void AddBowireCatalogue_Without_Provider_Yields_Null_Accessor()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var cfg = new ConfigurationBuilder().Build();

        services.AddBowireCatalogue(cfg);

        using var sp = services.BuildServiceProvider();
        var accessor = sp.GetRequiredService<BowireCatalogueProviderAccessor>();
        Assert.Null(accessor.Provider);
    }

    [Fact]
    public void AddBowireCatalogue_With_Local_Provider_Resolves_To_Local()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Discovery:Catalogue:Provider"] = "local",
                ["Bowire:Discovery:Catalogue:Local:Path"] = "/tmp/test-catalogue.json",
            })
            .Build();

        services.AddBowireCatalogue(cfg);

        using var sp = services.BuildServiceProvider();
        var accessor = sp.GetRequiredService<BowireCatalogueProviderAccessor>();
        Assert.NotNull(accessor.Provider);
        Assert.Equal("local", accessor.Provider!.Id);
    }

    [Fact]
    public void AddBowireCatalogue_Action_Overload_Works()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddBowireCatalogue(opts => opts.Provider = "http");

        using var sp = services.BuildServiceProvider();
        var accessor = sp.GetRequiredService<BowireCatalogueProviderAccessor>();
        Assert.NotNull(accessor.Provider);
        Assert.Equal("http", accessor.Provider!.Id);
    }
}

/// <summary>
/// Minimal mock <see cref="HttpMessageHandler"/> for the provider
/// tests. Each request is routed through the supplied delegate so
/// the test can assert request shape + return a canned response.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_handler(request, cancellationToken));
}
