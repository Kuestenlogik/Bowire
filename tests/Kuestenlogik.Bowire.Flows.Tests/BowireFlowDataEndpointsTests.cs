// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Flows.Tests;

/// <summary>
/// Row expansion over HTTP (#174) — what the in-browser flow runner asks
/// for before it runs a parameterised step.
/// </summary>
/// <remarks>
/// The rows come from the server so there is one definition of what a row
/// is. These tests are about the endpoint's half of that: the shape the
/// browser gets back, and the four ways a request is turned away.
/// </remarks>
public sealed class BowireFlowDataEndpointsTests
{
    private static async Task<IHost> BuildHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e =>
                           new BowireFlowDataEndpoints().MapEndpoints(e, basePath: string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ExpandAsync(object data)
    {
        using var host = await BuildHost();
        using var client = host.GetTestClient();
        var response = await client.PostAsJsonAsync(
            new Uri("/api/flows/data/expand", UriKind.Relative), data, TestContext.Current.CancellationToken);
        return await ReadAsync(response);
    }

    /// <summary>Post a body verbatim, for shapes an anonymous type cannot spell.</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body)> ExpandRawAsync(string json)
    {
        using var host = await BuildHost();
        using var client = host.GetTestClient();
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync(
            new Uri("/api/flows/data/expand", UriKind.Relative), content, TestContext.Current.CancellationToken);
        return await ReadAsync(response);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        return (response.StatusCode, doc.RootElement.Clone());
    }

    [Fact]
    public async Task Inline_Rows_Come_Back_With_Their_Columns()
    {
        var (status, body) = await ExpandAsync(new
        {
            inline = new[] { new { userId = "1" }, new { userId = "2" } },
            labelColumn = "userId",
        });

        Assert.Equal(HttpStatusCode.OK, status);
        var rows = body.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("1", rows[0].GetProperty("label").GetString());
        Assert.Equal("1", rows[0].GetProperty("values").GetProperty("userId").GetString());
        Assert.Equal("2", rows[1].GetProperty("values").GetProperty("userId").GetString());
    }

    [Fact]
    public async Task A_Generator_Produces_The_Same_Rows_It_Does_For_The_Cli()
    {
        // `var` is the generator's column name and a C# keyword, so this
        // one goes over the wire verbatim.
        var (status, body) = await ExpandRawAsync(
            """{"generator":{"kind":"range","var":"i","from":1,"to":3}}""");

        Assert.Equal(HttpStatusCode.OK, status);
        var rows = body.GetProperty("rows");
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal(["1", "2", "3"],
            rows.EnumerateArray().Select(r => r.GetProperty("values").GetProperty("i").GetString()));
        // Unlabelled rows fall back to the zero-based index.
        Assert.Equal(["0", "1", "2"],
            rows.EnumerateArray().Select(r => r.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task A_Csv_Source_Is_Refused_With_The_Reason_And_The_Way_Round_It()
    {
        var (status, body) = await ExpandAsync(new { csv = "fixtures/users.csv" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var error = body.GetProperty("error").GetString()!;
        // A workbench flow lives in flows.json and has no file to resolve
        // against; the reply says so and names the route that does work.
        Assert.Contains("flow file", error, StringComparison.Ordinal);
        Assert.Contains("bowire test", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Empty_Source_Is_Refused_In_The_Expanders_Own_Words()
    {
        var (status, body) = await ExpandAsync(new { });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        // The same sentence CI prints, so the workbench and the pipeline
        // do not describe one misconfiguration two ways.
        Assert.Contains("exactly one of inline / csv / generator",
            body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_Sources_At_Once_Are_Refused()
    {
        var (status, body) = await ExpandAsync(new
        {
            inline = new[] { new { a = "1" } },
            csv = "users.csv",
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("flow file", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }
}
