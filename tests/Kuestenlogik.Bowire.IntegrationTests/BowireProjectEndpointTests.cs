// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
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
/// Integration coverage for <c>BowireProjectEndpoints</c> (#172): the
/// workbench's Open-Folder / boot probe against
/// <c>GET /api/project</c>. Each test drives a TestServer and points the
/// endpoint at a per-test temp folder via <c>?path=</c> so the walk-up
/// discovery runs without touching the process working directory (no
/// <c>CwdSerialised</c> serialisation needed). Mirrors
/// <see cref="BowireMockConfigEndpointTests"/>.
/// </summary>
public sealed class BowireProjectEndpointTests : IDisposable
{
    private readonly string _tempRoot;

    public BowireProjectEndpointTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"bowire-project-ep-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private string WriteManifest(string json)
    {
        var dir = Path.Combine(_tempRoot, ".bowire");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "project.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task GET_returns_the_manifest_when_present()
    {
        WriteManifest("""
        {
          "version": 1,
          "name": "order-service",
          "sources": [ { "url": "https://api.example.com", "schemas": [ "./proto/orders.proto" ] } ],
          "suites": { "smoke": "./suites/smoke.json" },
          "security": { "auth": "./auth/login.flow.json", "scan": [ "owasp-api" ] },
          "rules": "./rules.json"
        }
        """);

        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/project?path=" + Uri.EscapeDataString(_tempRoot), UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("found").GetBoolean());
        Assert.Equal("order-service", root.GetProperty("name").GetString());
        Assert.Equal("https://api.example.com", root.GetProperty("sources")[0].GetProperty("url").GetString());
        Assert.Equal("./suites/smoke.json", root.GetProperty("suites").GetProperty("smoke").GetString());
        Assert.Equal("owasp-api", root.GetProperty("security").GetProperty("scan")[0].GetString());
        Assert.Equal("./rules.json", root.GetProperty("rules").GetString());
        // A clean manifest carries no soft-validation warnings.
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task GET_returns_404_found_false_when_no_manifest()
    {
        // The temp folder has no .bowire/project.json anywhere up the chain
        // (system temp has no Bowire ancestor).
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/project?path=" + Uri.EscapeDataString(_tempRoot), UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task GET_malformed_manifest_returns_400_and_never_throws()
    {
        WriteManifest("{ not valid json");

        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/project?path=" + Uri.EscapeDataString(_tempRoot), UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Invalid project file", body, StringComparison.OrdinalIgnoreCase);
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
                       app.UseEndpoints(e => e.MapBowireProjectEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .Build();
        await host.StartAsync();
        return host;
    }
}
