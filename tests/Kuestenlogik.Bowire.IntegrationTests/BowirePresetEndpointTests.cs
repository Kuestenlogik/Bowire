// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Integration coverage for <c>BowirePresetEndpoints</c> (#140). Drives
/// <c>GET</c> and <c>PUT /api/presets</c> through a TestServer with
/// <see cref="BowireUserContext"/> redirected to a per-test temp root,
/// so the round-trip through the disk store is exercised without
/// touching the developer's real <c>~/.bowire/</c>.
/// </summary>
[Collection("BowireUserContext")]
public sealed class BowirePresetEndpointTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _tempRoot;

    public BowirePresetEndpointTests()
    {
        _originalStore = BowireUserContext.Current;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"bowire-presets-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        BowireUserContext.Current = new TempStore(_tempRoot);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _originalStore;
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GET_missing_mode_returns_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/presets?workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("mode", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GET_no_file_returns_empty_array()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/presets?mode=discover&workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task PUT_then_GET_round_trips_preset_payload()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        const string payload = """
        [
          {"id":"p1","name":"Login","mode":"discover","request":{"verb":"POST","url":"https://x.example.com/login"}}
        ]
        """;
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var put = await client.PutAsync(
            new Uri("/api/presets?mode=discover&workspaceId=ws-1", UriKind.Relative),
            content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var get = await client.GetAsync(
            new Uri("/api/presets?mode=discover&workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var body = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("Login", doc.RootElement[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task PUT_invalid_json_returns_400_with_error_body()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");
        using var resp = await client.PutAsync(
            new Uri("/api/presets?mode=discover&workspaceId=ws-1", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Invalid JSON", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PUT_missing_mode_returns_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var content = new StringContent("[]", Encoding.UTF8, "application/json");
        using var resp = await client.PutAsync(
            new Uri("/api/presets?workspaceId=ws-1", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PUT_bad_mode_returns_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        // The mode must be a simple ascii slug — '/' trips the
        // SanitiseMode guard inside PresetStore.Save.
        using var content = new StringContent("[]", Encoding.UTF8, "application/json");
        using var resp = await client.PutAsync(
            new Uri("/api/presets?mode=bad/mode&workspaceId=ws-1", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GET_bad_mode_returns_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/presets?mode=bad*mode&workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Presets_isolated_per_mode()
    {
        // Two writes to different modes must not see each other's body.
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var discoverPayload = new StringContent(
            """[{"id":"p1","name":"d-only"}]""",
            Encoding.UTF8, "application/json");
        using var discoverResp = await client.PutAsync(
            new Uri("/api/presets?mode=discover&workspaceId=ws-1", UriKind.Relative),
            discoverPayload, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, discoverResp.StatusCode);

        using var benchPayload = new StringContent(
            """[{"id":"p2","name":"b-only"}]""",
            Encoding.UTF8, "application/json");
        using var benchResp = await client.PutAsync(
            new Uri("/api/presets?mode=benchmarks&workspaceId=ws-1", UriKind.Relative),
            benchPayload, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, benchResp.StatusCode);

        using var getDiscover = await client.GetAsync(
            new Uri("/api/presets?mode=discover&workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var discoverBody = await getDiscover.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var discoverDoc = JsonDocument.Parse(discoverBody);
        Assert.Equal("d-only", discoverDoc.RootElement[0].GetProperty("name").GetString());

        using var getBench = await client.GetAsync(
            new Uri("/api/presets?mode=benchmarks&workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var benchBody = await getBench.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var benchDoc = JsonDocument.Parse(benchBody);
        Assert.Equal("b-only", benchDoc.RootElement[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Presets_isolated_per_workspace()
    {
        // Workspace A's write must not leak into workspace B's slot.
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var aPayload = new StringContent(
            """[{"id":"p","name":"ws-a"}]""",
            Encoding.UTF8, "application/json");
        using var aResp = await client.PutAsync(
            new Uri("/api/presets?mode=discover&workspaceId=ws-a", UriKind.Relative),
            aPayload, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, aResp.StatusCode);

        using var getB = await client.GetAsync(
            new Uri("/api/presets?mode=discover&workspaceId=ws-b", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var bBody = await getB.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var bDoc = JsonDocument.Parse(bBody);
        Assert.Equal(0, bDoc.RootElement.GetArrayLength());
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
                       app.UseEndpoints(e => e.MapBowirePresetEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    private sealed class TempStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }
}
