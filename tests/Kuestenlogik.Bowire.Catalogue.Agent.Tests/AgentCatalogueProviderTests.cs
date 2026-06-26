// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using Kuestenlogik.Bowire.Catalogue.Agent;
using Kuestenlogik.Bowire.Sources;

namespace Kuestenlogik.Bowire.Catalogue.Agent.Tests;

/// <summary>
/// Tests for <see cref="AgentCatalogueProvider"/> — Phase E of the
/// catalogue-provider seam (#305 / depends on #128).
/// </summary>
public sealed class AgentCatalogueProviderTests
{
    [Fact]
    public void Id_And_Name_Match_Documented_Wire_Surface()
    {
        var provider = new AgentCatalogueProvider();
        Assert.Equal("agent", provider.Id);
        Assert.Equal("Bowire Agent hub", provider.Name);
    }

    [Fact]
    public void Discover_Picks_Up_Provider_From_Sibling_Assembly()
    {
        _ = new AgentCatalogueProvider();
        var providers = BowireCatalogueProviderRegistry.Discover();
        Assert.Contains("agent", providers.Keys);
    }

    [Fact]
    public async Task FetchAsync_Returns_Empty_When_Hub_Url_Is_Unset()
    {
        // Default behaviour while #128 is in flight: provider returns
        // empty so the workbench's "no catalogue" surface stays intact.
        var provider = new AgentCatalogueProvider(
            () => new BowireAgentCatalogueOptions(),
            () => new HttpClient());

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task FetchAsync_Parses_Stub_Response_Without_Hitting_The_Wire()
    {
        // Stub path lets installations validate their planned aggregator
        // payload against the wire-shape contract before #128 ships.
        var provider = new AgentCatalogueProvider(
            () => new BowireAgentCatalogueOptions
            {
                StubResponse = """
                    {
                      "version": 1,
                      "agents": [
                        {
                          "agentId": "surgewave-broker@eu-central",
                          "serviceName": "surgewave-broker",
                          "tags": ["env:prod"],
                          "entries": [
                            { "url": "https://broker.internal:7080", "protocols": ["grpc"] }
                          ]
                        }
                      ]
                    }
                    """,
            },
            () => new HttpClient());

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);

        Assert.Single(entries);
        Assert.Equal("https://broker.internal:7080", entries[0].Url);
        // Service name surfaces as the label when the entry doesn't
        // carry its own.
        Assert.Equal("surgewave-broker", entries[0].Name);
        // Tag composition includes agent + service + the agent's own.
        Assert.Contains("agent:surgewave-broker@eu-central", entries[0].Tags!);
        Assert.Contains("service:surgewave-broker", entries[0].Tags!);
        Assert.Contains("env:prod", entries[0].Tags!);
    }

    [Fact]
    public async Task FetchAsync_Hits_Hub_Catalogue_Endpoint_When_Url_Is_Set()
    {
        using var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(
                "https://hub.example.com/hub/agents/catalogue",
                req.RequestUri!.ToString());
            Assert.Equal(
                "Bearer bootstrap",
                req.Headers.GetValues("Authorization").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "version": 1,
                      "agents": [
                        {
                          "agentId": "a1",
                          "serviceName": "payments",
                          "entries": [{ "url": "https://payments:443" }]
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json"),
            };
        });

        var provider = new AgentCatalogueProvider(
            () => new BowireAgentCatalogueOptions
            {
                HubUrl = "https://hub.example.com",
                BootstrapToken = "bootstrap",
            },
            () => new HttpClient(handler, disposeHandler: false));

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Single(entries);
        Assert.Equal("https://payments:443", entries[0].Url);
    }

    [Fact]
    public async Task FetchAsync_Throws_On_Non_Success_Response()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var provider = new AgentCatalogueProvider(
            () => new BowireAgentCatalogueOptions { HubUrl = "https://hub.example.com" },
            () => new HttpClient(handler, disposeHandler: false));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.FetchAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ParseDocument_Drops_Empty_Url_Entries()
    {
        // Defence in depth: a hub aggregator could surface a partial
        // record while a registered agent is mid-restart.
        var json = """
            {
              "agents": [
                {
                  "agentId": "a1",
                  "entries": [
                    { "url": "" },
                    { "url": "https://valid:443" }
                  ]
                }
              ]
            }
            """;

        var entries = AgentCatalogueProvider.ParseDocument(json);

        Assert.Single(entries);
        Assert.Equal("https://valid:443", entries[0].Url);
    }
}

/// <summary>Mock <see cref="HttpMessageHandler"/> mirroring the core test pattern.</summary>
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
