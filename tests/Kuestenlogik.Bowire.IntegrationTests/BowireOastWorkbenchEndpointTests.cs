// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text.Json;
using Kuestenlogik.Bowire.Security.Scanner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// #486 — the /api/security/oast/* endpoints behind the Security rail's manual
/// OAST panel. They mount through the <see cref="Kuestenlogik.Bowire.Plugins.IBowireEndpointContribution"/>
/// seam and read <c>Bowire:Oast:Server</c> at resolution time, so these tests
/// pin both the honest not-configured behaviour and that a configured server
/// surfaces. The full plant-and-poll loop against a live server is covered in
/// the Oast package's server tests; here the focus is the endpoint contract.
/// </summary>
public sealed class BowireOastWorkbenchEndpointTests
{
    [Fact]
    public async Task Status_reports_not_configured_when_no_server_is_set()
    {
        await using var host = await CreateHost(oastServer: null);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/security/oast/status", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(doc.RootElement.GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Allocate_without_a_server_is_a_clear_409_not_a_500()
    {
        await using var host = await CreateHost(oastServer: null);
        var client = host.GetTestClient();

        var resp = await client.PostAsync(new Uri("/bowire/api/security/oast/allocate", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("bowire oast serve", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Poll_without_a_server_is_an_empty_feed_not_an_error()
    {
        await using var host = await CreateHost(oastServer: null);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/security/oast/poll", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, doc.RootElement.GetProperty("interactions").GetArrayLength());
    }

    [Fact]
    public async Task Status_reports_configured_and_the_server_domain()
    {
        await using var host = await CreateHost(oastServer: "https://oast.example.com");
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/bowire/api/security/oast/status", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(doc.RootElement.GetProperty("configured").GetBoolean());
        Assert.Equal("oast.example.com", doc.RootElement.GetProperty("server").GetString());
    }

    [Fact]
    public async Task Allocate_against_an_unreachable_server_is_a_502_not_a_crash()
    {
        // Configured but nothing listening — register fails. The panel must be
        // told "the server, not your request", so a 502 rather than a 500 or a
        // silent success.
        await using var host = await CreateHost(oastServer: "http://127.0.0.1:1");
        var client = host.GetTestClient();

        var resp = await client.PostAsync(new Uri("/bowire/api/security/oast/allocate", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    private static async Task<WebApplication> CreateHost(string? oastServer)
    {
        // Force-load the Scanner assembly so its endpoint + service
        // contributions are discovered by the MapBowire scan.
        _ = typeof(OastWorkbenchSession).Assembly;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (oastServer is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Oast:Server"] = oastServer,
            });
        }
        // Run the service-contribution scan so BowireOastWorkbenchServiceContribution
        // registers the session from config — the real path the workbench uses,
        // not a hand-registered stand-in.
        builder.Services.AddBowire();

        var app = builder.Build();
        app.UseStaticFiles();
        app.MapBowire("/bowire");

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
