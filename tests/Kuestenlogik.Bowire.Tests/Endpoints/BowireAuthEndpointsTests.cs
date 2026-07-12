// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// Integration coverage for <see cref="BowireAuthEndpoints"/> — the OAuth
/// CORS-bypass proxy (client_credentials / authorization_code / refresh_token
/// / custom-token), the static callback page, the in-memory cookie jar, and
/// the session-token relay. Drives a loopback Kestrel host that proxies to a
/// second stub "identity provider" host, so the form-building + pass-through +
/// upstream-error branches all execute. Joins CwdSerialised for CreateSlimBuilder
/// safety.
/// </summary>
[Collection("CwdSerialised")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope — apps + client disposed by the caller.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic.")]
public sealed class BowireAuthEndpointsTests
{
    private static readonly Uri TokenUri = new("/api/auth/oauth-token", UriKind.Relative);
    private static readonly Uri CodeUri = new("/api/auth/oauth-code-exchange", UriKind.Relative);
    private static readonly Uri RefreshUri = new("/api/auth/oauth-refresh", UriKind.Relative);
    private static readonly Uri CustomUri = new("/api/auth/custom-token", UriKind.Relative);

    private sealed record Hosts(WebApplication Auth, WebApplication Upstream, HttpClient Http, string UpstreamUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Auth.DisposeAsync().ConfigureAwait(false);
            await Upstream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Hosts> StartAsync(CancellationToken ct)
    {
        // Stub identity provider: /error → 500, everything else → a token JSON.
        var ub = WebApplication.CreateSlimBuilder();
        ub.Logging.ClearProviders();
        ub.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var upstream = ub.Build();
        upstream.Run(async ctx =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (path.Contains("error", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync("""{"error":"invalid_client"}""", ctx.RequestAborted);
                return;
            }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"access_token":"tok-123","token_type":"Bearer"}""", ctx.RequestAborted);
        });
        await upstream.StartAsync(ct).ConfigureAwait(false);
        var upstreamUrl = upstream.Urls.First();

        var ab = WebApplication.CreateSlimBuilder();
        ab.Logging.ClearProviders();
        ab.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        ab.Services.AddHttpClient();
        var auth = ab.Build();
        auth.MapBowireAuthEndpoints(new BowireOptions(), "");
        await auth.StartAsync(ct).ConfigureAwait(false);
        var http = new HttpClient { BaseAddress = new Uri(auth.Urls.First()) };
        return new Hosts(auth, upstream, http, upstreamUrl);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<string> BodyString(HttpResponseMessage resp, CancellationToken ct) =>
        await resp.Content.ReadAsStringAsync(ct);

    // ------------------------------ oauth-token ------------------------------

    [Fact]
    public async Task Token_success_passes_provider_body_through()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);

        using var resp = await h.Http.PostAsync(TokenUri, Json($$"""
            { "tokenUrl": "{{h.UpstreamUrl}}/token", "clientId": "cid", "clientSecret": "sec", "scope": "read", "audience": "api" }
            """), ct);
        resp.EnsureSuccessStatusCode();
        Assert.Contains("tok-123", await BodyString(resp, ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_upstream_error_maps_to_502()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);

        using var resp = await h.Http.PostAsync(TokenUri, Json($$"""
            { "tokenUrl": "{{h.UpstreamUrl}}/error", "clientId": "cid" }
            """), ct);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task Token_network_failure_maps_to_502()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);

        // Port 1 has nothing listening → connection refused → 502 upstream-error.
        using var resp = await h.Http.PostAsync(TokenUri, Json("""
            { "tokenUrl": "http://127.0.0.1:1/token", "clientId": "cid" }
            """), ct);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task Token_malformed_json_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var resp = await h.Http.PostAsync(TokenUri, Json("{ not json"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData("""{ "clientId": "cid" }""")]                                  // missing tokenUrl
    [InlineData("""{ "tokenUrl": "https://idp/token" }""")]                    // missing clientId
    [InlineData("""{ "tokenUrl": "not-a-url", "clientId": "cid" }""")]         // invalid tokenUrl
    public async Task Token_validation_failures_are_400(string body)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var resp = await h.Http.PostAsync(TokenUri, Json(body), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // --------------------------- oauth-code-exchange ---------------------------

    [Fact]
    public async Task CodeExchange_success_passes_through()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);

        using var resp = await h.Http.PostAsync(CodeUri, Json($$"""
            { "tokenUrl": "{{h.UpstreamUrl}}/token", "code": "abc", "clientId": "cid", "redirectUri": "https://app/cb", "codeVerifier": "v", "clientSecret": "s" }
            """), ct);
        resp.EnsureSuccessStatusCode();
        Assert.Contains("tok-123", await BodyString(resp, ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeExchange_missing_fields_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var resp = await h.Http.PostAsync(CodeUri, Json("""{ "tokenUrl": "https://idp/token", "code": "abc" }"""), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ------------------------------ oauth-refresh ------------------------------

    [Fact]
    public async Task Refresh_success_passes_through()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);

        using var resp = await h.Http.PostAsync(RefreshUri, Json($$"""
            { "tokenUrl": "{{h.UpstreamUrl}}/token", "refreshToken": "rt", "clientId": "cid", "clientSecret": "s", "scope": "read" }
            """), ct);
        resp.EnsureSuccessStatusCode();
        Assert.Contains("tok-123", await BodyString(resp, ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_missing_fields_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var resp = await h.Http.PostAsync(RefreshUri, Json("""{ "tokenUrl": "https://idp/token", "clientId": "cid" }"""), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ------------------------------ custom-token ------------------------------

    [Fact]
    public async Task CustomToken_success_with_method_headers_and_body()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);

        using var resp = await h.Http.PostAsync(CustomUri, Json($$"""
            {
              "url": "{{h.UpstreamUrl}}/custom",
              "method": "post",
              "contentType": "application/json",
              "body": "{\"grant\":\"x\"}",
              "headers": { "X-Api-Key": "k" }
            }
            """), ct);
        resp.EnsureSuccessStatusCode();
        Assert.Contains("tok-123", await BodyString(resp, ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomToken_upstream_error_maps_to_502()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var resp = await h.Http.PostAsync(CustomUri, Json($$"""{ "url": "{{h.UpstreamUrl}}/error" }"""), ct);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Theory]
    [InlineData("""{ "method": "GET" }""")]                 // missing url
    [InlineData("""{ "url": "not-a-url" }""")]              // invalid url
    public async Task CustomToken_validation_failures_are_400(string body)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var resp = await h.Http.PostAsync(CustomUri, Json(body), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ------------------------------ callback + jar + session ------------------------------

    [Fact]
    public async Task Callback_page_is_served_as_html()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);

        using var resp = await h.Http.GetAsync(new Uri("/oauth-callback", UriKind.Relative), ct);
        resp.EnsureSuccessStatusCode();
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains("postMessage", await BodyString(resp, ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CookieJar_get_and_delete_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        var env = "env-" + Guid.NewGuid().ToString("N");

        using (var get = await h.Http.GetAsync(new Uri($"/api/auth/cookie-jar?env={env}", UriKind.Relative), ct))
        {
            get.EnsureSuccessStatusCode();
            var body = JsonDocument.Parse(await BodyString(get, ct)).RootElement;
            Assert.Equal(env, body.GetProperty("env").GetString());
        }

        using var del = await h.Http.DeleteAsync(new Uri($"/api/auth/cookie-jar?env={env}", UriKind.Relative), ct);
        del.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task CookieJar_without_env_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var resp = await h.Http.GetAsync(new Uri("/api/auth/cookie-jar", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Session_anonymous_caller_reports_no_token()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);

        using var resp = await h.Http.GetAsync(new Uri("/api/auth/session", UriKind.Relative), ct);
        resp.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await BodyString(resp, ct)).RootElement;
        Assert.False(body.GetProperty("hasToken").GetBoolean());
        Assert.Equal("anonymous", body.GetProperty("reason").GetString());
    }
}
