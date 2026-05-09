// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Integration coverage for the channel / auth / plugin endpoint
/// surfaces. The existing <see cref="BowireEndpointTests"/> walks the
/// happy invoke / streaming / recordings paths; this file targets the
/// validation / not-found / shape-of-response branches that round out
/// the same endpoint files. Driven through the shared
/// <see cref="BowireTestFixture"/> so we re-use the same in-process
/// host without a second WebApplication startup tax.
/// </summary>
public sealed class EndpointCoverageTests : IClassFixture<BowireTestFixture>
{
    private readonly HttpClient _client;

    public EndpointCoverageTests(BowireTestFixture fixture) => _client = fixture.Client;

    // ---------- BowireChannelEndpoints ----------

    [Fact]
    public async Task ChannelOpen_EmptyBody_Returns400()
    {
        // `{}` parses to a body with null Service/Method and trips the
        // validation guard. Truly empty `""` would throw a JsonException
        // before validation — that's a separate codepath the endpoint
        // doesn't handle gracefully.
        using var req = new HttpRequestMessage(HttpMethod.Post,
            new Uri("/bowire/api/channel/open", UriKind.Relative))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ChannelOpen_MissingService_Returns400()
    {
        // service is required for the dispatch — payload without it
        // surfaces as a clean 400 with a descriptive error body.
        var body = """{"protocol":"grpc","method":"Greet"}""";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(new Uri("/bowire/api/channel/open", UriKind.Relative), content,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("service", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChannelSend_UnknownChannelId_Returns404()
    {
        var body = """{"message":"hello"}""";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/channel/does-not-exist/send", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ChannelSend_EmptyBody_Returns400()
    {
        // `{}` is the smallest payload the deserialiser accepts — body
        // has null Message → validation 400 before the channel lookup.
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/channel/anything/send", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ChannelClose_UnknownChannelId_Returns404()
    {
        using var content = new StringContent("", Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/channel/missing/close", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ChannelResponses_UnknownChannelId_Returns404()
    {
        var resp = await _client.GetAsync(
            new Uri("/bowire/api/channel/missing/responses", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---------- BowireAuthEndpoints ----------

    [Fact]
    public async Task CookieJar_Get_MissingEnv_Returns400()
    {
        var resp = await _client.GetAsync(
            new Uri("/bowire/api/auth/cookie-jar", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CookieJar_Get_WithEnv_ReturnsEnvelope()
    {
        var resp = await _client.GetAsync(
            new Uri("/bowire/api/auth/cookie-jar?env=dev", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("dev", doc.RootElement.GetProperty("env").GetString());
        Assert.True(doc.RootElement.TryGetProperty("cookies", out _));
    }

    [Fact]
    public async Task CookieJar_Delete_MissingEnv_Returns400()
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete,
            new Uri("/bowire/api/auth/cookie-jar", UriKind.Relative));

        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CookieJar_Delete_WithEnv_ReportsClearedCount()
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete,
            new Uri("/bowire/api/auth/cookie-jar?env=dev", UriKind.Relative));

        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("dev", doc.RootElement.GetProperty("env").GetString());
        Assert.True(doc.RootElement.TryGetProperty("cleared", out _));
    }

    [Theory]
    [InlineData("/bowire/api/auth/oauth-token")]
    [InlineData("/bowire/api/auth/oauth-code-exchange")]
    [InlineData("/bowire/api/auth/oauth-refresh")]
    [InlineData("/bowire/api/auth/custom-token")]
    public async Task OAuth_Endpoints_Empty_Object_Are_Rejected(string path)
    {
        // `{}` parses but the body's required fields are null —
        // validation lives at the front of every OAuth endpoint and
        // returns 400 before the upstream http-fetch.
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(new Uri(path, UriKind.Relative), content,
            TestContext.Current.CancellationToken);

        // 4xx is the contract — accept any 400-range so individual
        // endpoints can return BadRequest / UnsupportedMediaType etc.
        // depending on how the body flows through their validator.
        Assert.True((int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500,
            $"Expected 4xx for empty body on {path}, got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task OauthCallback_GET_ReturnsHtmlShell()
    {
        var resp = await _client.GetAsync(
            new Uri("/bowire/oauth-callback", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/html", resp.Content.Headers.ContentType?.MediaType ?? "", StringComparison.Ordinal);
        var html = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- BowirePluginEndpoints ----------

    [Fact]
    public async Task Plugins_List_ReturnsEnvelopeWithPluginsArray()
    {
        var resp = await _client.GetAsync(
            new Uri("/bowire/api/plugins", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("plugins").ValueKind);
    }

    [Fact]
    public async Task Plugins_Protocols_ReturnsLoadedAndCatalog()
    {
        var resp = await _client.GetAsync(
            new Uri("/bowire/api/plugins/protocols", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        // 'loaded' is what BowireProtocolRegistry currently has registered;
        // 'catalog' is the static protocol-id → package-id map. Both are
        // documented contract — pin the keys so accidental rename surfaces.
        Assert.True(doc.RootElement.TryGetProperty("loaded", out var loaded));
        Assert.Equal(JsonValueKind.Array, loaded.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("catalog", out var catalog));
        Assert.Equal(JsonValueKind.Object, catalog.ValueKind);
        // The fixture loads grpc + signalr — they have to show up in 'loaded'.
        var loadedIds = loaded.EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Contains("grpc", loadedIds);
        Assert.Contains("signalr", loadedIds);
    }

    [Fact]
    public async Task Plugins_Install_MissingPackageId_Returns400()
    {
        var body = "{}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/plugins/install", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("packageId", json, StringComparison.OrdinalIgnoreCase);
    }
}
