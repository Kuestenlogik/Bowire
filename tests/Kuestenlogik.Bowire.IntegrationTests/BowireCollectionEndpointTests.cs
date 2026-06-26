// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Integration coverage for <c>BowireCollectionEndpoints</c> (#295) —
/// exercises every public route of the disk-backed collection store via
/// a TestServer with <see cref="CollectionStore.StorePath"/> pinned to
/// a per-test temp file so the disk round-trip is real but isolated.
/// </summary>
[Collection("BowireUserContext")]
public sealed class BowireCollectionEndpointTests : IDisposable
{
    private readonly string _originalPath;
    private readonly string _tempPath;

    public BowireCollectionEndpointTests()
    {
        _originalPath = CollectionStore.StorePath;
        _tempPath = Path.Combine(
            Path.GetTempPath(),
            $"bowire-collections-test-{Guid.NewGuid():N}",
            "collections.json");
        CollectionStore.StorePath = _tempPath;
    }

    public void Dispose()
    {
        CollectionStore.StorePath = _originalPath;
        var dir = Path.GetDirectoryName(_tempPath);
        if (dir is not null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GET_returns_empty_envelope_when_no_file()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/collections", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array,
            doc.RootElement.GetProperty("collections").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("collections").GetArrayLength());
    }

    [Fact]
    public async Task PUT_then_GET_round_trips_collection_payload()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        const string payload = """
        {"collections":[{"id":"c1","name":"Smoke","requests":[]}]}
        """;
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var put = await client.PutAsync(
            new Uri("/api/collections", UriKind.Relative),
            content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var putBody = await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var putDoc = JsonDocument.Parse(putBody);
        Assert.True(putDoc.RootElement.GetProperty("saved").GetBoolean());

        using var get = await client.GetAsync(
            new Uri("/api/collections", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var body = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var arr = doc.RootElement.GetProperty("collections");
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("Smoke", arr[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task PUT_invalid_json_returns_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");
        using var resp = await client.PutAsync(
            new Uri("/api/collections", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Invalid JSON", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PUT_empty_body_returns_400()
    {
        // Empty / whitespace bodies trip the ArgumentException branch in
        // CollectionStore.Save before reaching JsonDocument.Parse.
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var content = new StringContent("   ", Encoding.UTF8, "application/json");
        using var resp = await client.PutAsync(
            new Uri("/api/collections", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task DELETE_returns_cleared_true_and_resets_store()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        // Seed the store first so DELETE has something to clear.
        using var seedBody = new StringContent(
            """{"collections":[{"id":"to-clear","name":"Junk"}]}""",
            Encoding.UTF8, "application/json");
        using var seed = await client.PutAsync(
            new Uri("/api/collections", UriKind.Relative),
            seedBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        using var del = await client.DeleteAsync(
            new Uri("/api/collections", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var delBody = await del.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var delDoc = JsonDocument.Parse(delBody);
        Assert.True(delDoc.RootElement.GetProperty("cleared").GetBoolean());

        // GET should now return the empty envelope.
        using var get = await client.GetAsync(
            new Uri("/api/collections", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var body = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("collections").GetArrayLength());
    }

    private static async Task<IHost> BuildHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireCollectionEndpoints(
                           new BowireOptions(), basePath: string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .Build();
        await host.StartAsync();
        return host;
    }
}
