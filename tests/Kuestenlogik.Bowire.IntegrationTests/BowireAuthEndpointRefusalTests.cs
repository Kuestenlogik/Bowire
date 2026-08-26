// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
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
/// The auth endpoints behind the environment Auth tab — what they refuse, and
/// the two that answer without leaving the machine.
/// </summary>
/// <remarks>
/// <para>
/// Three of these post a client secret to an identity provider the operator
/// named. Every refusal asserted here happens before that request is built, so
/// a half-filled form in the workbench cannot turn into a credential sent
/// somewhere unintended — a mis-parsed <c>tokenUrl</c> is exactly the shape of
/// mistake that ships a secret to the wrong host and reports a normal-looking
/// error.
/// </para>
/// <para>
/// The token exchanges themselves need a provider and are covered where one
/// exists; nothing in this suite reaches a network.
/// </para>
/// </remarks>
public sealed class BowireAuthEndpointRefusalTests
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
                       app.UseEndpoints(e => e.MapBowireAuthEndpoints(new BowireOptions(), basePath: string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Post(
        IHost host, string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await host.GetTestClient().PostAsync(
            new Uri(path, UriKind.Relative), content, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (resp.StatusCode, doc.RootElement.Clone());
    }

    // ---- client-credentials ----

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("""{"clientId":"abc"}""")]                     // no tokenUrl
    [InlineData("""{"tokenUrl":"https://idp.example.com/token"}""")]  // no clientId
    public async Task A_Client_Credentials_Request_Missing_Its_Essentials_Is_A_400(string body)
    {
        using var host = await BuildHost();

        var (status, _) = await Post(host, "/api/auth/oauth-token", body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task A_Token_Url_That_Is_Not_An_Absolute_Url_Is_Named_As_Such()
    {
        // The distinct problem type matters: "missing" and "malformed" are
        // fixed differently, and the Auth tab shows the title verbatim.
        using var host = await BuildHost();

        var (status, body) = await Post(host, "/api/auth/oauth-token",
            """{"tokenUrl":"idp.example.com/token","clientId":"abc","clientSecret":"s3cret"}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("urn:bowire:auth:invalid-token-url", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_Refusal_Never_Echoes_The_Client_Secret()
    {
        // Problem documents end up in browser consoles and bug reports.
        using var host = await BuildHost();

        var (_, body) = await Post(host, "/api/auth/oauth-token",
            """{"tokenUrl":"not a url","clientId":"abc","clientSecret":"s3cret-do-not-log"}""");

        Assert.DoesNotContain("s3cret-do-not-log", body.ToString(), StringComparison.Ordinal);
    }

    // ---- authorization-code exchange and refresh ----

    [Theory]
    [InlineData("/api/auth/oauth-code-exchange")]
    [InlineData("/api/auth/oauth-refresh")]
    [InlineData("/api/auth/custom-token")]
    public async Task Every_Token_Endpoint_Refuses_A_Body_That_Is_Not_Json(string path)
    {
        using var host = await BuildHost();

        var (status, _) = await Post(host, path, "{ not json");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Theory]
    [InlineData("/api/auth/oauth-code-exchange")]
    [InlineData("/api/auth/oauth-refresh")]
    [InlineData("/api/auth/custom-token")]
    public async Task Every_Token_Endpoint_Refuses_An_Empty_Body(string path)
    {
        using var host = await BuildHost();

        var (status, _) = await Post(host, path, "null");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task A_Code_Exchange_Without_A_Code_Is_Refused()
    {
        using var host = await BuildHost();

        var (status, _) = await Post(host, "/api/auth/oauth-code-exchange",
            """{"tokenUrl":"https://idp.example.com/token","clientId":"abc"}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task A_Refresh_Without_A_Refresh_Token_Is_Refused()
    {
        using var host = await BuildHost();

        var (status, _) = await Post(host, "/api/auth/oauth-refresh",
            """{"tokenUrl":"https://idp.example.com/token","clientId":"abc"}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    // ---- the cookie jar ----

    [Fact]
    public async Task Reading_The_Cookie_Jar_Without_An_Environment_Is_A_400()
    {
        // The jar is per-environment; a missing id would otherwise read as
        // "the environment with the empty name", which always looks empty.
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/api/auth/cookie-jar", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task An_Environment_That_Never_Stored_A_Cookie_Has_An_Empty_Jar()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/api/auth/cookie-jar?env=never-used", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("never-used", doc.RootElement.GetProperty("env").GetString());
        Assert.Empty(doc.RootElement.GetProperty("cookies").EnumerateArray());
    }

    [Fact]
    public async Task Clearing_The_Cookie_Jar_Without_An_Environment_Is_A_400()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().DeleteAsync(
            new Uri("/api/auth/cookie-jar", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Clearing_An_Empty_Jar_Reports_That_Nothing_Was_Cleared()
    {
        // "Log out" on an environment that was never logged in is a normal
        // click, and the flag is what the UI confirms it with — false here
        // because there was no jar to remove, which is not an error.
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().DeleteAsync(
            new Uri("/api/auth/cookie-jar?env=never-used", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(doc.RootElement.GetProperty("cleared").GetBoolean());
    }

    // ---- the redirect landing page ----

    [Fact]
    public async Task The_Oauth_Callback_Serves_A_Page_The_Browser_Can_Render()
    {
        // The identity provider redirects a real browser here at the end of an
        // authorization-code flow; anything but HTML leaves the operator
        // staring at a download prompt or a blank tab.
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/oauth-callback", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
        var html = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("<script", html, StringComparison.OrdinalIgnoreCase);
    }
}
