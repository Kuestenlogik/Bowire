// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.IntegrationTests.Hubs;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Kuestenlogik.Bowire.Protocol.SignalR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Integration coverage for the OAuth proxy endpoints (token /
/// code-exchange / refresh / custom-token). The endpoints resolve
/// their <see cref="HttpClient"/> through <see cref="IHttpClientFactory"/>'s
/// named-client "bowire-oauth", so the fixture overrides that
/// client's <see cref="HttpMessageHandler"/> with a recording mock.
/// No real network listener — every upstream call is short-circuited
/// in-process.
/// </summary>
// CA1034 nested-type warning is expected here — the fixture +
// recording handler are tightly bound to this test class and only
// public because xUnit's IClassFixture machinery needs to see them.
#pragma warning disable CA1034
public sealed class OAuthEndpointTests : IClassFixture<OAuthEndpointTests.OAuthFixture>
{
    private readonly OAuthFixture _fixture;
    private readonly HttpClient _client;

    public OAuthEndpointTests(OAuthFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _fixture.MockHandler.Reset();
    }

    // ---------- /api/auth/oauth-token (client_credentials) ----------

    [Fact]
    public async Task ClientCredentials_HappyPath_PassesThroughTokenJson()
    {
        _fixture.MockHandler.QueueResponse(HttpStatusCode.OK,
            """{"access_token":"cc-abc","token_type":"Bearer","expires_in":3600}""");
        var body = """
            {
              "tokenUrl": "https://idp.example.com/token",
              "clientId": "my-app",
              "clientSecret": "shh",
              "scope": "read write",
              "audience": "api://demo"
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/auth/oauth-token", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("cc-abc", doc.RootElement.GetProperty("access_token").GetString());

        // Wire-format contract: form fields + target URL the endpoint sent upstream.
        var captured = Assert.Single(_fixture.MockHandler.Requests);
        Assert.Equal(new Uri("https://idp.example.com/token"), captured.Url);
        Assert.Contains("grant_type=client_credentials", captured.Body, StringComparison.Ordinal);
        Assert.Contains("client_id=my-app", captured.Body, StringComparison.Ordinal);
        Assert.Contains("client_secret=shh", captured.Body, StringComparison.Ordinal);
        Assert.Contains("scope=read+write", captured.Body, StringComparison.Ordinal);
        Assert.Contains("audience=api%3A%2F%2Fdemo", captured.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientCredentials_IdpReturns401_Surfaces502_WithDetails()
    {
        _fixture.MockHandler.QueueResponse(HttpStatusCode.Unauthorized,
            """{"error":"invalid_client"}""");
        var body = """
            {"tokenUrl":"https://idp.example.com/token","clientId":"bad","clientSecret":"wrong"}
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/auth/oauth-token", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        // Bowire wraps upstream non-2xx as 502 + a details envelope so
        // the UI can surface "the IdP rejected you" without conflating
        // it with "Bowire is down".
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.Contains("HTTP 401", doc.RootElement.GetProperty("error").GetString() ?? "", StringComparison.Ordinal);
        Assert.Contains("invalid_client", doc.RootElement.GetProperty("details").GetString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientCredentials_InvalidTokenUrl_Returns400_WithoutCallingUpstream()
    {
        // Validation rejects non-absolute URLs before opening any
        // HttpClient — the mock handler should record zero requests.
        var body = """{"tokenUrl":"not-a-url","clientId":"app"}""";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/auth/oauth-token", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("absolute", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_fixture.MockHandler.Requests);
    }

    // ---------- /api/auth/oauth-code-exchange (authorization_code + PKCE) ----------

    [Fact]
    public async Task CodeExchange_HappyPath_PassesThroughAccessAndRefreshTokens()
    {
        _fixture.MockHandler.QueueResponse(HttpStatusCode.OK,
            """{"access_token":"ac-xyz","refresh_token":"rt-789","expires_in":3600}""");
        var body = """
            {
              "tokenUrl": "https://idp.example.com/token",
              "code": "auth-code-42",
              "redirectUri": "http://localhost:5080/oauth-callback",
              "clientId": "my-app",
              "clientSecret": "shh",
              "codeVerifier": "pkce-verifier-string"
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/auth/oauth-code-exchange", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ac-xyz", doc.RootElement.GetProperty("access_token").GetString());
        Assert.Equal("rt-789", doc.RootElement.GetProperty("refresh_token").GetString());

        var captured = Assert.Single(_fixture.MockHandler.Requests);
        Assert.Contains("grant_type=authorization_code", captured.Body, StringComparison.Ordinal);
        Assert.Contains("code=auth-code-42", captured.Body, StringComparison.Ordinal);
        Assert.Contains("code_verifier=pkce-verifier-string", captured.Body, StringComparison.Ordinal);
        Assert.Contains("client_secret=shh", captured.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeExchange_IdpReturns400_Surfaces502()
    {
        _fixture.MockHandler.QueueResponse(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant"}""");
        var body = """
            {
              "tokenUrl": "https://idp.example.com/token",
              "code": "expired",
              "redirectUri": "http://localhost:5080/oauth-callback",
              "clientId": "my-app"
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/auth/oauth-code-exchange", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("HTTP 400", json, StringComparison.Ordinal);
        Assert.Contains("invalid_grant", json, StringComparison.Ordinal);
    }

    // ---------- /api/auth/oauth-refresh ----------

    [Fact]
    public async Task Refresh_HappyPath_ReturnsNewAccessToken()
    {
        _fixture.MockHandler.QueueResponse(HttpStatusCode.OK,
            """{"access_token":"refreshed-9","expires_in":600}""");
        var body = """
            {
              "tokenUrl": "https://idp.example.com/token",
              "refreshToken": "rt-old",
              "clientId": "my-app",
              "clientSecret": "shh",
              "scope": "read"
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/auth/oauth-refresh", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("refreshed-9", doc.RootElement.GetProperty("access_token").GetString());

        var captured = Assert.Single(_fixture.MockHandler.Requests);
        Assert.Contains("grant_type=refresh_token", captured.Body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=rt-old", captured.Body, StringComparison.Ordinal);
        Assert.Contains("scope=read", captured.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_IdpReturns401_Surfaces502()
    {
        _fixture.MockHandler.QueueResponse(HttpStatusCode.Unauthorized,
            """{"error":"invalid_grant","error_description":"Refresh token expired"}""");
        var body = """
            {
              "tokenUrl": "https://idp.example.com/token",
              "refreshToken": "rt-expired",
              "clientId": "my-app"
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/auth/oauth-refresh", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Refresh token expired", json, StringComparison.Ordinal);
    }

    // ---------- /api/auth/custom-token (generic HTTP proxy) ----------

    [Fact]
    public async Task CustomToken_HappyPath_DefaultMethod_IsPost()
    {
        _fixture.MockHandler.QueueResponse(HttpStatusCode.OK,
            """{"token":"custom-yzx"}""");
        var body = """
            {
              "url": "https://api.example.com/login",
              "headers": { "X-Api-Key": "k1" }
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/auth/custom-token", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("custom-yzx", doc.RootElement.GetProperty("token").GetString());

        var captured = Assert.Single(_fixture.MockHandler.Requests);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.True(captured.Headers.TryGetValue("X-Api-Key", out var apiKey));
        Assert.Equal("k1", apiKey);
    }

    [Fact]
    public async Task CustomToken_InvalidUrl_Returns400_WithoutCallingUpstream()
    {
        var body = """{"url":"not absolute"}""";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/auth/custom-token", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(_fixture.MockHandler.Requests);
    }

    // ---- Fixture: own WebApplication so we can swap the bowire-oauth handler ----

    public sealed class OAuthFixture : IAsyncLifetime
    {
        public IHost Host { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public RecordingMockHandler MockHandler { get; } = new();

        public async ValueTask InitializeAsync()
        {
            // Same plugin-assembly preload as BowireTestFixture so the
            // protocol registry's discovery scan finds gRPC + SignalR.
            EnsureProtocolAssembliesLoaded();

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            builder.Services.AddGrpc();
            builder.Services.AddGrpcReflection();
            builder.Services.AddSignalR();
            builder.Services.AddBowire();

            // Swap the primary HttpMessageHandler on the named client the
            // OAuth endpoints resolve through. Any HttpClient pulled from
            // factory.CreateClient("bowire-oauth") now goes through
            // RecordingMockHandler instead of the real network.
            builder.Services.AddHttpClient("bowire-oauth")
                .ConfigurePrimaryHttpMessageHandler(() => MockHandler);

            var app = builder.Build();
            app.MapGrpcService<Services.GreeterService>();
            app.MapGrpcReflectionService();
            app.MapHub<ChatHub>("/chathub");
            app.MapBowire("/bowire");

            await app.StartAsync(TestContext.Current.CancellationToken);
            Host = app;
            Client = app.GetTestClient();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EnsureProtocolAssembliesLoaded()
        {
            _ = typeof(BowireGrpcProtocol).Assembly;
            _ = typeof(BowireSignalRProtocol).Assembly;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.StopAsync(TestContext.Current.CancellationToken);
            Host.Dispose();
            MockHandler.Dispose();
        }
    }

    /// <summary>
    /// Records every request that flows through and replies with the
    /// next queued <c>(status, body)</c> pair. Tests reset the queue
    /// + recordings in their constructor so each test sees a clean slate.
    /// </summary>
    public sealed class RecordingMockHandler : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(HttpStatusCode Status, string Body)> _responses = new();
        private readonly object _captureLock = new();
        private readonly List<CapturedRequest> _captured = new();

        public IReadOnlyList<CapturedRequest> Requests
        {
            get { lock (_captureLock) return [.. _captured]; }
        }

        public void QueueResponse(HttpStatusCode status, string body) =>
            _responses.Enqueue((status, body));

        public void Reset()
        {
            lock (_captureLock) _captured.Clear();
            while (_responses.TryDequeue(out _)) { /* drain */ }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bodyText = "";
            if (request.Content is not null)
            {
                bodyText = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in request.Headers)
                headers[k] = string.Join(",", v);
            if (request.Content?.Headers is { } ch)
            {
                foreach (var (k, v) in ch) headers[k] = string.Join(",", v);
            }

            lock (_captureLock)
            {
                _captured.Add(new CapturedRequest(
                    request.Method, request.RequestUri ?? new Uri("about:blank"),
                    bodyText, headers));
            }

            if (!_responses.TryDequeue(out var canned))
                canned = (HttpStatusCode.OK, """{"access_token":"default"}""");

            var resp = new HttpResponseMessage(canned.Status)
            {
                Content = new StringContent(canned.Body, Encoding.UTF8, "application/json")
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return resp;
        }
    }

    public sealed record CapturedRequest(
        HttpMethod Method,
        Uri Url,
        string Body,
        IReadOnlyDictionary<string, string> Headers);
}
#pragma warning restore CA1034
