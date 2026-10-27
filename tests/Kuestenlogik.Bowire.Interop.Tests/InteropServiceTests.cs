// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Interop.Tests;

/// <summary>
/// The Interop service as the workbench reaches it: the JSON transcoding of
/// its rpcs, mapped by the contribution seam under Bowire's base path.
/// </summary>
public sealed class InteropServiceTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        // The contribution scan reads loaded assemblies; make sure this one is.
        _ = typeof(InteropRpc).Assembly;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Services.AddBowire();
        _app = builder.Build();
        _app.MapBowire("bowire");
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Import_Is_Reachable_As_Json_Under_The_Bowire_Base_Path()
    {
        using var response = await _http.PostAsJsonAsync("/bowire/api/v1/interop/import", new
        {
            files = new[] { new { path = "api.http", content = "### Health\nGET https://api.example.com/health\n" } },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = json.RootElement;
        Assert.Equal("http", root.GetProperty("format").GetString());
        var item = root.GetProperty("collections")[0].GetProperty("items")[0];
        // The transcoded JSON uses the workbench's own field names.
        Assert.Equal("Health", item.GetProperty("name").GetString());
        Assert.Equal("https://api.example.com/health", item.GetProperty("serverUrl").GetString());
        Assert.Equal("Unary", item.GetProperty("methodType").GetString());
    }

    [Fact]
    public async Task A_Bad_File_Comes_Back_As_400_With_The_Reason()
    {
        using var response = await _http.PostAsJsonAsync("/bowire/api/v1/interop/import", new
        {
            files = new[] { new { path = "notes.txt", content = "just prose" } },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Not a format Bowire imports", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_Is_Reachable_As_Json()
    {
        using var response = await _http.PostAsJsonAsync("/bowire/api/v1/interop/export-http", new
        {
            collections = new[] { new { name = "c", items = new[] { new { name = "Ping", protocol = "rest", service = "https://x.test/ping", method = "GET", methodType = "Unary" } } } },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("GET https://x.test/ping", json.RootElement.GetProperty("http").GetString(), StringComparison.Ordinal);
    }
}
