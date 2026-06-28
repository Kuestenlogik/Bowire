// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.Help;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Kuestenlogik.Bowire.Protocol.SignalR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// #154 Phase 1 — verifies the Help SPI endpoints behave correctly in
/// both states: no provider registered (501 Not Implemented for the
/// three content endpoints; 200 + available:false for the capability
/// probe), and a registered provider (200 + topic / search results).
/// Phase 2 ships the actual Kuestenlogik.Bowire.Help package; these
/// tests pin the contract that package will fulfil.
/// </summary>
public sealed class BowireHelpEndpointTests
{
    // Endpoints emit camelCase (BowireEndpointHelpers convention).
    // Reading back into Pascal-cased records needs case-insensitive
    // matching — set once and reuse.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task Available_ReturnsFalse_WhenNoProviderRegistered()
    {
        await using var host = await CreateHost(configureServices: null);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/help/available", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var payload = await resp.Content.ReadFromJsonAsync<AvailableResponse>(JsonOpts, TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        Assert.False(payload!.Available);
    }

    [Fact]
    public async Task Available_ReturnsTrue_WhenProviderRegistered()
    {
        await using var host = await CreateHost(services =>
            services.AddSingleton<IBowireHelpProvider, StubHelpProvider>());
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/help/available", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var payload = await resp.Content.ReadFromJsonAsync<AvailableResponse>(JsonOpts, TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        Assert.True(payload!.Available);
    }

    [Fact]
    public async Task Topics_Returns501_WhenNoProviderRegistered()
    {
        await using var host = await CreateHost(configureServices: null);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/help/topics", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
        // The 501 body is a problem-details document with the URN
        // discriminator the UI keys on.
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("urn:bowire:help:provider-not-registered", body);
        Assert.Contains("Kuestenlogik.Bowire.Help", body);
    }

    [Fact]
    public async Task TopicById_Returns501_WhenNoProviderRegistered()
    {
        await using var host = await CreateHost(configureServices: null);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/help/topic/anything", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
    }

    [Fact]
    public async Task Search_Returns501_WhenNoProviderRegistered()
    {
        await using var host = await CreateHost(configureServices: null);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/help/search?q=foo", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
    }

    [Fact]
    public async Task TopicById_Returns200_WhenProviderHasTopic()
    {
        await using var host = await CreateHost(services =>
            services.AddSingleton<IBowireHelpProvider, StubHelpProvider>());
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/help/topic/quickstart", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var topic = await resp.Content.ReadFromJsonAsync<HelpTopic>(JsonOpts, TestContext.Current.CancellationToken);
        Assert.NotNull(topic);
        Assert.Equal("quickstart", topic!.Id);
        Assert.Equal("Quickstart", topic.Title);
        Assert.Contains("# Quickstart", topic.Markdown);
    }

    [Fact]
    public async Task TopicById_Returns404_WhenProviderLacksTopic()
    {
        await using var host = await CreateHost(services =>
            services.AddSingleton<IBowireHelpProvider, StubHelpProvider>());
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/help/topic/no-such-thing", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("urn:bowire:help:topic-not-found", body);
    }

    [Fact]
    public async Task Topics_ReturnsSummariesList_WhenProviderRegistered()
    {
        await using var host = await CreateHost(services =>
            services.AddSingleton<IBowireHelpProvider, StubHelpProvider>());
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/help/topics", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var payload = await resp.Content.ReadFromJsonAsync<TopicsResponse>(JsonOpts, TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        Assert.Equal(2, payload!.Topics.Count);
        Assert.Contains(payload.Topics, t => t.Id == "quickstart");
        Assert.Contains(payload.Topics, t => t.Id == "recordings");
    }

    [Fact]
    public async Task Search_ReturnsRankedHits_WhenProviderRegistered()
    {
        await using var host = await CreateHost(services =>
            services.AddSingleton<IBowireHelpProvider, StubHelpProvider>());
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/help/search?q=record", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var payload = await resp.Content.ReadFromJsonAsync<SearchResponse>(JsonOpts, TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        Assert.Single(payload!.Hits);
        Assert.Equal("recordings", payload.Hits[0].Id);
    }

    private static async Task<WebApplication> CreateHost(Action<IServiceCollection>? configureServices)
    {
        _ = typeof(BowireGrpcProtocol).Assembly;
        _ = typeof(BowireSignalRProtocol).Assembly;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseStaticFiles();
        app.MapBowire("/bowire");

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class StubHelpProvider : IBowireHelpProvider
    {
        private static readonly HelpTopic[] _topics =
        [
            new("quickstart", "Quickstart", null, "# Quickstart\n\nPick a method.", "<h1>Quickstart</h1><p>Pick a method.</p>", null),
            new("recordings", "Recordings", null, "# Recordings\n\nRecord a session.", "<h1>Recordings</h1><p>Record a session.</p>", "features"),
        ];

        public HelpTopic? GetTopic(string id) =>
            _topics.FirstOrDefault(t => t.Id == id);

        public IReadOnlyList<HelpSearchHit> Search(string query, int limit = 20)
        {
            if (string.IsNullOrWhiteSpace(query)) return [];
            return _topics
                .Where(t => t.Markdown.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || t.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .Select(t => new HelpSearchHit(t.Id, t.Title, t.Markdown, 1.0))
                .ToList();
        }

        public IReadOnlyList<HelpTopicSummary> ListTopics() =>
            _topics.Select(t => new HelpTopicSummary(t.Id, t.Title, t.Summary, t.CategoryId)).ToList();
    }

    private sealed record AvailableResponse(bool Available);
    private sealed record TopicsResponse(List<HelpTopicSummary> Topics);
    private sealed record SearchResponse(List<HelpSearchHit> Hits);
}
