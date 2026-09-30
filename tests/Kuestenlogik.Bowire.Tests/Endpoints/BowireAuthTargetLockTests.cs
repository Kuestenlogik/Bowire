// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// The auth helpers that call identity providers on the caller's behalf —
/// <c>/api/auth/oauth-token</c>, <c>/oauth-code-exchange</c>,
/// <c>/oauth-refresh</c>, <c>/custom-token</c> — honour
/// <see cref="BowireTargetPolicy.ForAuth"/>: on a locked host a token URL
/// outside <see cref="BowireOptions.ServerUrls"/> +
/// <see cref="BowireOptions.AllowedAuthUrls"/> gets a 403 before anything is
/// sent, a listed identity provider still works, and an unlocked host proxies
/// anywhere as before. Also covers that the restricted auth client does not
/// follow redirects.
/// </summary>
[Collection("CwdSerialised")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope — apps + client disposed by the caller.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic.")]
public sealed class BowireAuthTargetLockTests
{
    private const string Foreign = "http://169.254.169.254/latest/meta-data";

    private sealed class Hosts(WebApplication auth, WebApplication idp, HttpClient http) : IAsyncDisposable
    {
        private int _idpHits;
        public WebApplication Auth { get; } = auth;
        public WebApplication Idp { get; } = idp;
        public HttpClient Http { get; } = http;
        public string IdpUrl => Idp.Urls.First();
        public int IdpHits => Volatile.Read(ref _idpHits);
        public void Hit() => Interlocked.Increment(ref _idpHits);

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Auth.DisposeAsync().ConfigureAwait(false);
            await Idp.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts a stub identity provider and an auth host. <paramref name="configure"/>
    /// receives the options and the provider's base URL, so a test can list it.
    /// </summary>
    private static async Task<Hosts> StartAsync(Action<BowireOptions, string> configure, CancellationToken ct)
    {
        Hosts? hosts = null;
        var ib = WebApplication.CreateSlimBuilder();
        ib.Logging.ClearProviders();
        ib.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var idp = ib.Build();
        idp.Run(async ctx =>
        {
            hosts?.Hit();
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"access_token":"tok-123","token_type":"Bearer"}""", ctx.RequestAborted);
        });
        await idp.StartAsync(ct).ConfigureAwait(false);

        var options = new BowireOptions { Mode = BowireMode.Standalone };
        configure(options, idp.Urls.First());

        var ab = WebApplication.CreateSlimBuilder();
        ab.Logging.ClearProviders();
        ab.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        ab.Services.AddHttpClient();
        var auth = ab.Build();
        auth.MapBowireAuthEndpoints(options, "");
        await auth.StartAsync(ct).ConfigureAwait(false);

        hosts = new Hosts(auth, idp, new HttpClient { BaseAddress = new Uri(auth.Urls.First()) });
        return hosts;
    }

    private static void LockedWithIdp(BowireOptions o, string idpUrl)
    {
        o.LockServerUrl = true;
        o.ServerUrls.Add("https://api.bowire.test");
        o.AllowedAuthUrls.Add(idpUrl);
    }

    public static TheoryData<string> Routes => new()
    {
        "/api/auth/oauth-token",
        "/api/auth/oauth-code-exchange",
        "/api/auth/oauth-refresh",
        "/api/auth/custom-token",
    };

    /// <summary>A body the given route accepts, aimed at <paramref name="tokenUrl"/>.</summary>
    private static StringContent BodyFor(string route, string tokenUrl)
    {
        object body = route switch
        {
            "/api/auth/oauth-token" => new { tokenUrl, clientId = "app", clientSecret = "s" },
            "/api/auth/oauth-code-exchange" => new { tokenUrl, code = "c", redirectUri = "http://localhost/cb", clientId = "app" },
            "/api/auth/oauth-refresh" => new { tokenUrl, refreshToken = "r", clientId = "app" },
            _ => new { url = tokenUrl, method = "POST" },
        };
        return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
        Assert.Equal(BowireTargetPolicy.RefusedProblemType, body.GetProperty("type").GetString());
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Locked_host_refuses_a_token_url_outside_the_allowlists(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(LockedWithIdp, ct);

        using var resp = await h.Http.PostAsync(new Uri(route, UriKind.Relative), BodyFor(route, Foreign), ct);

        await AssertRefusedAsync(resp, ct);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Locked_host_refuses_a_foreign_host_hiding_behind_the_idp_as_userinfo(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(LockedWithIdp, ct);
        var sneaky = "http://" + new Uri(h.IdpUrl).Authority + "@169.254.169.254/token";

        using var resp = await h.Http.PostAsync(new Uri(route, UriKind.Relative), BodyFor(route, sneaky), ct);

        await AssertRefusedAsync(resp, ct);
        Assert.Equal(0, h.IdpHits);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Locked_host_proxies_to_a_listed_identity_provider(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(LockedWithIdp, ct);

        using var resp = await h.Http.PostAsync(new Uri(route, UriKind.Relative), BodyFor(route, h.IdpUrl + "/token"), ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("tok-123", await resp.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal(1, h.IdpHits);
    }

    [Fact]
    public async Task Locked_host_without_AllowedAuthUrls_refuses_an_external_idp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync((o, _) =>
        {
            o.LockServerUrl = true;
            o.ServerUrls.Add("https://api.bowire.test");
        }, ct);

        using var resp = await h.Http.PostAsync(new Uri("/api/auth/oauth-token", UriKind.Relative),
            BodyFor("/api/auth/oauth-token", h.IdpUrl + "/token"), ct);

        await AssertRefusedAsync(resp, ct);
        Assert.Equal(0, h.IdpHits);
    }

    [Fact]
    public async Task AllowedAuthUrls_alone_restrict_the_auth_helpers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync((o, idp) => o.AllowedAuthUrls.Add(idp), ct);

        using (var refused = await h.Http.PostAsync(new Uri("/api/auth/custom-token", UriKind.Relative),
                   BodyFor("/api/auth/custom-token", Foreign), ct))
            await AssertRefusedAsync(refused, ct);
        using var ok = await h.Http.PostAsync(new Uri("/api/auth/custom-token", UriKind.Relative),
            BodyFor("/api/auth/custom-token", h.IdpUrl + "/token"), ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Unlocked_host_proxies_anywhere_as_before(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync((_, _) => { }, ct);

        using var resp = await h.Http.PostAsync(new Uri(route, UriKind.Relative), BodyFor(route, h.IdpUrl + "/token"), ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ------------------------- client registration ---------------------------

    private static HttpClientHandler PrimaryHandler(IServiceProvider sp, string name)
    {
        HttpMessageHandler? handler = sp.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
        while (handler is DelegatingHandler d) handler = d.InnerHandler;
        return Assert.IsType<HttpClientHandler>(handler, exactMatch: false);
    }

    [Fact]
    public async Task AddBowire_registers_a_non_redirecting_client_for_restricted_auth_calls()
    {
        var services = new ServiceCollection();
        services.AddBowire();
        await using var sp = services.BuildServiceProvider();

        // Restricted: a 302 from an allowed token endpoint must not carry the
        // call to a host the policy never checked.
        Assert.False(PrimaryHandler(sp, BowireEndpointHelpers.OAuthLockedClient).AllowAutoRedirect);
        // Unrestricted: unchanged.
        Assert.True(PrimaryHandler(sp, BowireEndpointHelpers.OAuthClient).AllowAutoRedirect);
    }

    [Fact]
    public void Auth_client_name_follows_the_policy()
    {
        Assert.Equal(BowireEndpointHelpers.OAuthClient,
            BowireEndpointHelpers.OAuthClientName(BowireTargetPolicy.Unrestricted));
        var locked = new BowireOptions { LockServerUrl = true };
        Assert.Equal(BowireEndpointHelpers.OAuthLockedClient,
            BowireEndpointHelpers.OAuthClientName(BowireTargetPolicy.ForAuth(locked)));
    }
}
