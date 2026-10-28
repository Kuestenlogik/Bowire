// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.DataSets.Tests;

/// <summary>The DataSets service as the workbench reaches it: JSON transcoding under Bowire's base path.</summary>
public sealed class DataSetsServiceTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        // The contribution scan reads loaded assemblies; make sure this one is.
        _ = typeof(DataSetRpc).Assembly;
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
    public async Task Parse_Is_Reachable_As_Json()
    {
        using var response = await _http.PostAsJsonAsync("/bowire/api/v1/datasets/parse",
            new { fileName = "rows.csv", content = "id,name\n1,Ada\n" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var row = json.RootElement.GetProperty("rows")[0];
        Assert.Equal(2, row.GetProperty("sourceLine").GetInt32());
        Assert.Equal("Ada", row.GetProperty("values").GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_Malformed_File_Is_A_400_Naming_The_Line()
    {
        using var response = await _http.PostAsJsonAsync("/bowire/api/v1/datasets/parse",
            new { fileName = "rows.csv", content = "id,name\n1\n" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("rows.csv line 2", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
