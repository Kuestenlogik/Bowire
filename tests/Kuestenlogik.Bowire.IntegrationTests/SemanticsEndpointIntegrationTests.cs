// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Integration smoke tests for the Phase-3 endpoints, running against
/// the shared <see cref="BowireTestFixture"/> (full Bowire host with
/// every installed protocol plugin wired through the normal startup
/// path). The fully-isolated AddBowire(...) scenarios live in
/// <see cref="BowireSemanticsEndpointsTests"/>; this fixture-based pair
/// only exercises the routes that don't need a seeded annotation store
/// — the UI-extensions enumeration and the bundle-asset endpoint.
/// </summary>
public sealed class SemanticsEndpointIntegrationTests : IClassFixture<BowireTestFixture>
{
    private readonly BowireTestFixture _fixture;

    public SemanticsEndpointIntegrationTests(BowireTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Effective_Endpoint_Returns_Empty_When_No_Annotations()
    {
        // The shared fixture doesn't call AddBowire, so the annotation
        // store isn't in DI; the endpoint must degrade gracefully to
        // an empty-array payload instead of 500-ing.
        var response = await _fixture.Client.GetAsync(
            new Uri("/bowire/api/semantics/effective?service=any&method=any", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("any", doc.RootElement.GetProperty("service").GetString());
        Assert.Equal("any", doc.RootElement.GetProperty("method").GetString());
        Assert.Empty(doc.RootElement.GetProperty("annotations").EnumerateArray());
    }

    [Fact]
    public async Task UiExtensions_Endpoint_Lists_The_Built_In_MapLibre_Extension()
    {
        // The discovery sweep loads MapLibreExtension at first endpoint
        // touch; reset the cache so this test sees a fresh discovery
        // even when another fixture has already poked at the endpoint.
        Kuestenlogik.Bowire.Endpoints.BowireSemanticsEndpoints.ResetCachedRegistryForTests();

        var response = await _fixture.Client.GetAsync(
            new Uri("/bowire/api/ui/extensions", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var ids = doc.RootElement.GetProperty("extensions")
            .EnumerateArray()
            .Select(e => e.GetProperty("id").GetString())
            .ToList();
        Assert.Contains("kuestenlogik.maplibre", ids);
    }

    [Fact]
    public async Task UiExtensions_Asset_Endpoint_Serves_The_Map_Widget_Bundle()
    {
        Kuestenlogik.Bowire.Endpoints.BowireSemanticsEndpoints.ResetCachedRegistryForTests();

        var response = await _fixture.Client.GetAsync(
            new Uri("/bowire/api/ui/extensions/kuestenlogik.maplibre/map.js",
                UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // Phase-3 widget registers against coordinate.wgs84 — the string
        // appears in the bundle source, so it survives the embed.
        Assert.Contains("coordinate.wgs84", body, StringComparison.Ordinal);
    }
}
