// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Kuestenlogik.Bowire.Semantics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// HTTP-level tests for the Phase-3 endpoints:
/// <c>/api/semantics/effective</c> and <c>/api/ui/extensions[…]</c>.
/// Boots a tiny WebApplication via <c>UseTestServer</c> so the routes
/// run inside their normal endpoint-routing pipeline; the alternative
/// (calling the static <c>MapBowireSemanticsEndpoints</c> against a
/// mocked IEndpointRouteBuilder) would skip the JSON serialiser surface
/// that the JS-side router actually consumes.
/// </summary>
public sealed class BowireSemanticsEndpointsTests
{
    private static readonly string[] s_coordinateKindStrings =
    [
        "coordinate.latitude",
        "coordinate.longitude",
    ];

    private static async Task<WebApplication> BuildAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // SchemaHintsPath = "" disables the user-local disk file so
        // the test seeds annotations deterministically without touching
        // ~/.bowire.
        builder.Services.AddBowire(opts => opts.SchemaHintsPath = string.Empty);

        var app = builder.Build();
        app.MapBowire("/bowire");
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    [Fact]
    public async Task Effective_Endpoint_Returns_Matching_Annotations()
    {
        var app = await BuildAppAsync();
        await using (app)
        {
            var store = app.Services.GetRequiredService<LayeredAnnotationStore>();
            store.AutoDetectorLayer.Set(
                AnnotationKey.ForSingleType("harbor.HarborService", "WatchCrane", "$.position.lat"),
                BuiltInSemanticTags.CoordinateLatitude);
            store.AutoDetectorLayer.Set(
                AnnotationKey.ForSingleType("harbor.HarborService", "WatchCrane", "$.position.lon"),
                BuiltInSemanticTags.CoordinateLongitude);
            // Unrelated annotation under a different method — verifies
            // the endpoint filters by (service, method).
            store.AutoDetectorLayer.Set(
                AnnotationKey.ForSingleType("harbor.HarborService", "OtherMethod", "$.position.lat"),
                BuiltInSemanticTags.CoordinateLatitude);

            var client = app.GetTestClient();
            var response = await client.GetAsync(
                new Uri("/bowire/api/semantics/effective?service=harbor.HarborService&method=WatchCrane", UriKind.Relative),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var root = doc.RootElement;

            Assert.Equal("harbor.HarborService", root.GetProperty("service").GetString());
            Assert.Equal("WatchCrane", root.GetProperty("method").GetString());

            var annotations = root.GetProperty("annotations").EnumerateArray().ToList();
            Assert.Equal(2, annotations.Count);

            foreach (var ann in annotations)
            {
                Assert.Equal("*", ann.GetProperty("messageType").GetString());
                Assert.Equal("auto", ann.GetProperty("source").GetString());
                var sem = ann.GetProperty("semantic").GetString();
                Assert.Contains(sem, s_coordinateKindStrings);
            }
        }
    }

    [Fact]
    public async Task Effective_Endpoint_Returns_Empty_Array_For_Unknown_Pair()
    {
        var app = await BuildAppAsync();
        await using (app)
        {
            var client = app.GetTestClient();
            var response = await client.GetAsync(
                new Uri("/bowire/api/semantics/effective?service=does.not.exist&method=nope", UriKind.Relative),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Empty(doc.RootElement.GetProperty("annotations").EnumerateArray());
        }
    }

    [Fact]
    public async Task Effective_Endpoint_Returns_BadRequest_When_Service_Missing()
    {
        var app = await BuildAppAsync();
        await using (app)
        {
            var client = app.GetTestClient();
            var response = await client.GetAsync(
                new Uri("/bowire/api/semantics/effective?method=anything", UriKind.Relative),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Effective_Endpoint_Source_Field_Reflects_User_Override()
    {
        var app = await BuildAppAsync();
        await using (app)
        {
            var store = app.Services.GetRequiredService<LayeredAnnotationStore>();
            var key = AnnotationKey.ForSingleType("svc", "m", "$.x");
            store.AutoDetectorLayer.Set(key, BuiltInSemanticTags.CoordinateLatitude);
            // User edit at the session layer — wins over auto.
            store.UserSessionLayer.Set(key, BuiltInSemanticTags.None);

            var client = app.GetTestClient();
            var response = await client.GetAsync(
                new Uri("/bowire/api/semantics/effective?service=svc&method=m", UriKind.Relative),
                TestContext.Current.CancellationToken);

            using var doc = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var ann = doc.RootElement.GetProperty("annotations").EnumerateArray().Single();
            Assert.Equal("none", ann.GetProperty("semantic").GetString());
            Assert.Equal("user", ann.GetProperty("source").GetString());
        }
    }

    [Fact]
    public async Task UiExtensions_Lists_MapLibre_BuiltIn()
    {
        var app = await BuildAppAsync();
        await using (app)
        {
            // Reset the static cache so a previous test's registry
            // doesn't leak. The discovery sweep finds MapLibreExtension
            // because Kuestenlogik.Bowire.IntegrationTests references
            // Kuestenlogik.Bowire which carries the [BowireExtension]
            // type.
            Kuestenlogik.Bowire.Endpoints.BowireSemanticsEndpoints.ResetCachedRegistryForTests();

            var client = app.GetTestClient();
            var response = await client.GetAsync(
                new Uri("/bowire/api/ui/extensions", UriKind.Relative),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            var extensions = doc.RootElement.GetProperty("extensions").EnumerateArray().ToList();
            var map = extensions.SingleOrDefault(e
                => e.GetProperty("id").GetString() == "kuestenlogik.maplibre");
            Assert.True(map.ValueKind == JsonValueKind.Object,
                "MapLibre extension descriptor not present in /api/ui/extensions");

            Assert.Equal("1.x", map.GetProperty("bowireApi").GetString());
            var kinds = map.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()).ToList();
            Assert.Contains("coordinate.wgs84", kinds);

            var caps = map.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToList();
            Assert.Contains("viewer", caps);
            Assert.Contains("editor", caps);

            var bundle = map.GetProperty("bundleUrl").GetString();
            Assert.NotNull(bundle);
            Assert.EndsWith("/api/ui/extensions/kuestenlogik.maplibre/map.js", bundle);

            var styles = map.GetProperty("stylesUrl").GetString();
            Assert.NotNull(styles);
            Assert.EndsWith("/api/ui/extensions/kuestenlogik.maplibre/maplibre-gl.css", styles);
        }
    }

    [Fact]
    public async Task UiExtensions_Serves_Bundle_Asset()
    {
        var app = await BuildAppAsync();
        await using (app)
        {
            Kuestenlogik.Bowire.Endpoints.BowireSemanticsEndpoints.ResetCachedRegistryForTests();

            var client = app.GetTestClient();
            var response = await client.GetAsync(
                new Uri("/bowire/api/ui/extensions/kuestenlogik.maplibre/map.js", UriKind.Relative),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/javascript", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            // The widget bundle calls register(...) and pins coordinate.wgs84.
            Assert.Contains("coordinate.wgs84", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UiExtensions_Asset_Endpoint_Returns_NotFound_For_Unknown_Extension()
    {
        var app = await BuildAppAsync();
        await using (app)
        {
            var client = app.GetTestClient();
            var response = await client.GetAsync(
                new Uri("/bowire/api/ui/extensions/unknown.id/anything.js", UriKind.Relative),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task UiExtensions_Asset_Endpoint_Rejects_Path_Outside_Whitelist()
    {
        var app = await BuildAppAsync();
        await using (app)
        {
            Kuestenlogik.Bowire.Endpoints.BowireSemanticsEndpoints.ResetCachedRegistryForTests();

            var client = app.GetTestClient();
            var response = await client.GetAsync(
                new Uri("/bowire/api/ui/extensions/kuestenlogik.maplibre/random-file.txt", UriKind.Relative),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task UiExtensions_Asset_Endpoint_Serves_MapLibre_License()
    {
        var app = await BuildAppAsync();
        await using (app)
        {
            Kuestenlogik.Bowire.Endpoints.BowireSemanticsEndpoints.ResetCachedRegistryForTests();

            var client = app.GetTestClient();
            var response = await client.GetAsync(
                new Uri("/bowire/api/ui/extensions/kuestenlogik.maplibre/LICENSE", UriKind.Relative),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("MapLibre", body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
