// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// #810 — the view store an MCP App is hosted from: what it serves, and the
/// policy it serves it under.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope — the host record disposes its client.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic.")]
public sealed class BowireMcpAppEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] s_apiDomain = ["https://api.example.test"];

    private static Uri U(string path) => new(path, UriKind.Relative);

    [Fact]
    public async Task A_Posted_View_Is_Served_Under_Its_Own_Sandboxed_Policy()
    {
        await using var host = await Host.StartAsync(Ct);

        var post = await host.Http.PostAsJsonAsync("/bowire/api/ui/mcp-apps/views",
            new { html = "<p>board</p>", csp = new { connectDomains = s_apiDomain } }, Ct);
        post.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await post.Content.ReadAsStringAsync(Ct));
        var url = created.RootElement.GetProperty("url").GetString()!;
        Assert.StartsWith("/bowire/api/ui/mcp-apps/views/", url, StringComparison.Ordinal);

        var view = await host.Http.GetAsync(U(url), Ct);
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        Assert.Equal("<p>board</p>", await view.Content.ReadAsStringAsync(Ct));
        Assert.Equal("text/html", view.Content.Headers.ContentType!.MediaType);
        var policy = string.Join(";", view.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("sandbox allow-scripts allow-forms", policy, StringComparison.Ordinal);
        Assert.Contains("connect-src https://api.example.test", policy, StringComparison.Ordinal);
        Assert.Equal("no-store", view.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task An_Unknown_View_Is_Not_Found_And_An_Empty_One_Is_Refused()
    {
        await using var host = await Host.StartAsync(Ct);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.GetAsync(U("/bowire/api/ui/mcp-apps/views/0000"), Ct)).StatusCode);
        var empty = await host.Http.PostAsJsonAsync("/bowire/api/ui/mcp-apps/views", new { html = "" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task The_Bridge_Is_Served_As_Script()
    {
        await using var host = await Host.StartAsync(Ct);

        var bridge = await host.Http.GetAsync(U("/bowire/api/ui/mcp-apps/app-bridge.js?v=1"), Ct);

        Assert.Equal(HttpStatusCode.OK, bridge.StatusCode);
        Assert.Equal("text/javascript", bridge.Content.Headers.ContentType!.MediaType);
        Assert.Contains("BowireMcpAppBridge", await bridge.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public void An_App_That_Declares_Nothing_Reaches_Nothing()
    {
        var policy = BowireMcpAppEndpoints.PolicyFor(null);

        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("connect-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'unsafe-inline';", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void Declared_Domains_Are_Granted_And_Anything_Else_Is_Dropped()
    {
        var policy = BowireMcpAppEndpoints.PolicyFor(new McpAppCsp(
            ConnectDomains: ["https://api.example.test", "wss://*.example.test:8443", "'unsafe-eval'", "https://x.test; script-src *"],
            ResourceDomains: ["https://cdn.example.test"]));

        Assert.Contains("connect-src https://api.example.test wss://*.example.test:8443;", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'unsafe-inline' https://cdn.example.test", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("x.test", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Base_Uri_List_Is_Read_Under_The_Spec_Name_And_The_Csharp_Sdk_Name()
    {
        Assert.Contains("base-uri https://a.example.test;", BowireMcpAppEndpoints.PolicyFor(new McpAppCsp(BaseUriDomains: ["https://a.example.test"])), StringComparison.Ordinal);
        Assert.Contains("base-uri https://b.example.test;", BowireMcpAppEndpoints.PolicyFor(new McpAppCsp(BaseUris: ["https://b.example.test"])), StringComparison.Ordinal);
    }

    private sealed record Host(WebApplication App, HttpClient Http) : IAsyncDisposable
    {
        public static async Task<Host> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.MapBowireMcpAppEndpoints("/bowire");
            await app.StartAsync(ct);
            return new Host(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
