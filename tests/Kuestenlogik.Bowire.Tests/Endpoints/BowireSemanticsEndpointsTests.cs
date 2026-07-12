// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Semantics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// Integration coverage for <see cref="BowireSemanticsEndpoints"/> (frame-
/// semantics Phase 3/4) — effective-schema lookup, the tiered annotation
/// write / delete surface, and the UI-extension catalogue. Drives a loopback
/// Kestrel host with a session-only <see cref="LayeredAnnotationStore"/> so the
/// tier routing (session ok / user + project disabled → 404), validation
/// branches, and the graceful no-store degrade paths all execute. Joins
/// CwdSerialised for CreateSlimBuilder safety against cwd/env mutators.
/// </summary>
[Collection("CwdSerialised")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope — app + client disposed by the caller.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic.")]
public sealed class BowireSemanticsEndpointsTests
{
    private static readonly Uri AnnotationUri = new("/api/semantics/annotation", UriKind.Relative);
    private static readonly Uri ExtensionsUri = new("/api/ui/extensions", UriKind.Relative);

    private sealed record Host(WebApplication App, HttpClient Http, LayeredAnnotationStore? Store, string? CleanupFile) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.DisposeAsync().ConfigureAwait(false);
            if (CleanupFile is not null)
            {
                try { if (File.Exists(CleanupFile)) File.Delete(CleanupFile); } catch (IOException) { /* best-effort */ }
            }
        }
    }

    // Session-only store: user + project file layers null → those tiers 404.
    private static LayeredAnnotationStore NewSessionOnlyStore() => new(
        new InMemoryAnnotationLayer(),
        userFileLayer: null,
        projectFileLayer: null,
        new InMemoryAnnotationLayer(),
        pluginHints: (_, _) => Array.Empty<Annotation>());

    private static async Task<Host> StartHostAsync(
        CancellationToken ct, bool wireStore = true, LayeredAnnotationStore? store = null, string? cleanupFile = null)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));

        if (wireStore)
        {
            store ??= NewSessionOnlyStore();
            b.Services.AddSingleton(store);
            b.Services.AddSingleton<IAnnotationStore>(store);
        }
        var app = b.Build();
        app.MapBowireSemanticsEndpoints("");
        await app.StartAsync(ct).ConfigureAwait(false);
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return new Host(app, http, wireStore ? store : null, cleanupFile);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage resp, CancellationToken ct) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement.Clone();

    // -------------------------- effective-schema GET --------------------------

    [Fact]
    public async Task Effective_requires_service_and_method()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using var resp = await host.Http.GetAsync(new Uri("/api/semantics/effective?service=S", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Effective_returns_empty_when_no_store()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct, wireStore: false);

        using var resp = await host.Http.GetAsync(new Uri("/api/semantics/effective?service=S&method=M", UriKind.Relative), ct);
        resp.EnsureSuccessStatusCode();
        var body = await ReadJson(resp, ct);
        Assert.Equal(0, body.GetProperty("annotations").GetArrayLength());
    }

    [Fact]
    public async Task Effective_surfaces_a_matching_session_annotation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        // Seed a session-tier annotation on (S, M).
        host.Store!.UserSessionLayer.Set(
            new AnnotationKey("S", "M", AnnotationKey.Wildcard, "$.lat"),
            new SemanticTag("coordinate.wgs84"));

        using var resp = await host.Http.GetAsync(new Uri("/api/semantics/effective?service=S&method=M", UriKind.Relative), ct);
        resp.EnsureSuccessStatusCode();
        var body = await ReadJson(resp, ct);
        var ann = body.GetProperty("annotations");
        Assert.Equal(1, ann.GetArrayLength());
        Assert.Equal("$.lat", ann[0].GetProperty("jsonPath").GetString());
        Assert.Equal("coordinate.wgs84", ann[0].GetProperty("semantic").GetString());
        Assert.Equal("user", ann[0].GetProperty("source").GetString());
    }

    // ------------------------------ annotation POST ------------------------------

    [Fact]
    public async Task Post_session_annotation_returns_effective_tag()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using var resp = await host.Http.PostAsync(AnnotationUri, Json("""
            { "service": "S", "method": "M", "jsonPath": "$.lat", "semantic": "coordinate.wgs84", "scope": "session" }
            """), ct);
        resp.EnsureSuccessStatusCode();
        var body = await ReadJson(resp, ct);
        Assert.Equal("coordinate.wgs84", body.GetProperty("semantic").GetString());
        Assert.Equal("user", body.GetProperty("source").GetString());
    }

    [Theory]
    [InlineData("user")]
    [InlineData("project")]
    public async Task Post_to_disabled_tier_is_404(string scope)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using var resp = await host.Http.PostAsync(AnnotationUri, Json($$"""
            { "service": "S", "method": "M", "jsonPath": "$.x", "semantic": "duration.iso8601", "scope": "{{scope}}" }
            """), ct);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Post_with_unknown_scope_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using var resp = await host.Http.PostAsync(AnnotationUri, Json("""
            { "service": "S", "method": "M", "jsonPath": "$.x", "semantic": "duration.iso8601", "scope": "banana" }
            """), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_missing_required_field_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        // Missing 'service' → validation failure.
        using var resp = await host.Http.PostAsync(AnnotationUri, Json("""
            { "method": "M", "jsonPath": "$.x", "semantic": "duration.iso8601", "scope": "session" }
            """), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_malformed_json_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using var resp = await host.Http.PostAsync(AnnotationUri, Json("{ not json"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_without_store_is_404()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct, wireStore: false);

        using var resp = await host.Http.PostAsync(AnnotationUri, Json("""
            { "service": "S", "method": "M", "jsonPath": "$.x", "semantic": "duration.iso8601", "scope": "session" }
            """), ct);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Post_user_tier_persists_to_file_and_reloads()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = Path.Combine(Path.GetTempPath(), "bowire-hints-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new LayeredAnnotationStore(
            new InMemoryAnnotationLayer(),
            userFileLayer: new JsonFileAnnotationLayer(file),
            projectFileLayer: null,
            new InMemoryAnnotationLayer(),
            pluginHints: (_, _) => Array.Empty<Annotation>());
        await using var host = await StartHostAsync(ct, store: store, cleanupFile: file);

        using var resp = await host.Http.PostAsync(AnnotationUri, Json("""
            { "service": "S", "method": "M", "jsonPath": "$.when", "semantic": "duration.iso8601", "scope": "user" }
            """), ct);
        resp.EnsureSuccessStatusCode();
        var body = await ReadJson(resp, ct);
        Assert.Equal("duration.iso8601", body.GetProperty("semantic").GetString());

        // The user-tier write flushed to disk (EnsureFileLayerLoaded → Set → SaveAsync).
        Assert.True(File.Exists(file), "user-tier write should persist the hints file");
        var persisted = await File.ReadAllTextAsync(file, ct);
        Assert.Contains("duration.iso8601", persisted, StringComparison.Ordinal);
    }

    // ----------------------------- annotation DELETE -----------------------------

    [Fact]
    public async Task Delete_session_annotation_returns_post_delete_effective()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);
        host.Store!.UserSessionLayer.Set(
            new AnnotationKey("S", "M", AnnotationKey.Wildcard, "$.lat"),
            new SemanticTag("coordinate.wgs84"));

        using var req = new HttpRequestMessage(HttpMethod.Delete, AnnotationUri)
        {
            Content = Json("""{ "service": "S", "method": "M", "jsonPath": "$.lat", "scope": "session" }"""),
        };
        using var resp = await host.Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await ReadJson(resp, ct);
        // Nothing survives at a lower tier → semantic is null after delete.
        Assert.True(body.GetProperty("semantic").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Delete_without_store_is_404()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct, wireStore: false);

        using var req = new HttpRequestMessage(HttpMethod.Delete, AnnotationUri)
        {
            Content = Json("""{ "service": "S", "method": "M", "jsonPath": "$.lat", "scope": "session" }"""),
        };
        using var resp = await host.Http.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_invalid_scope_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using var req = new HttpRequestMessage(HttpMethod.Delete, AnnotationUri)
        {
            Content = Json("""{ "service": "S", "method": "M", "jsonPath": "$.lat", "scope": "nope" }"""),
        };
        using var resp = await host.Http.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ----------------------------- UI-extension catalogue -----------------------------

    [Fact]
    public async Task Extensions_listing_returns_an_array()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using var resp = await host.Http.GetAsync(ExtensionsUri, ct);
        resp.EnsureSuccessStatusCode();
        var body = await ReadJson(resp, ct);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("extensions").ValueKind);
    }

    [Fact]
    public async Task Extension_asset_for_unknown_id_is_404()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(ct);

        using var resp = await host.Http.GetAsync(new Uri("/api/ui/extensions/does-not-exist/map.js", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
