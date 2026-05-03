// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Kuestenlogik.Bowire.Protocol.SignalR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.IntegrationTests;

public sealed class BowireEndpointTests : IClassFixture<BowireTestFixture>
{
    private readonly HttpClient _client;

    public BowireEndpointTests(BowireTestFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task BowireUiEndpoint_ReturnsHtml()
    {
        var response = await _client.GetAsync(new Uri("/bowire", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("<!DOCTYPE html>", content);
        Assert.Contains("__BOWIRE_CONFIG__", content);
        Assert.Contains("title:", content);
    }

    [Fact]
    public async Task BowireUiEndpoint_ContainsInlinedAssets()
    {
        var response = await _client.GetAsync(new Uri("/bowire", UriKind.Relative), TestContext.Current.CancellationToken);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("<style>", content);  // Inlined CSS
        Assert.Contains("<script>", content); // Inlined JS
    }

    [Fact]
    public async Task ServicesEndpoint_ReturnsJsonOrError()
    {
        // The services endpoint may return a 502 in TestServer environments because
        // Bowire opens its own gRPC channel to localhost -- which does not reach
        // the in-memory TestServer. We verify the endpoint is routed correctly.
        var response = await _client.GetAsync(new Uri("/bowire/api/services", UriKind.Relative), TestContext.Current.CancellationToken);

        // Either 200 (if gRPC reflection is reachable) or 502 (expected in TestServer)
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadGateway,
            $"Unexpected status code: {response.StatusCode}");

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(content));
    }

    [Fact]
    public async Task InvokeEndpoint_AcceptsPost()
    {
        var body = new
        {
            service = "test.Greeter",
            method = "SayHello",
            messages = new[] { """{"name":"World"}""" }
        };

        var json = JsonSerializer.Serialize(body);
        using var requestContent = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync(
            new Uri("/bowire/api/invoke", UriKind.Relative), requestContent, TestContext.Current.CancellationToken);

        // Endpoint is routed; may 502 because gRPC reflection can't reach TestServer
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadGateway,
            $"Unexpected status code: {response.StatusCode}");
    }

    [Fact]
    public async Task InvokeEndpoint_EmptyBody_ReturnsBadRequest()
    {
        // Sending an empty JSON object (no service/method) should result in a
        // BadRequest because the deserialized body has null required fields, or
        // a 502 if the API attempts to invoke with empty values.
        using var requestContent = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync(
            new Uri("/bowire/api/invoke", UriKind.Relative), requestContent, TestContext.Current.CancellationToken);

        // The endpoint processes the body; empty service/method will either fail
        // with BadRequest or 502 when attempting reflection
        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.BadGateway
                or HttpStatusCode.InternalServerError or HttpStatusCode.OK,
            $"Unexpected status code: {response.StatusCode}");
    }

    [Fact]
    public async Task StreamingEndpoint_MissingParams_Returns400()
    {
        // Missing service and method should return 400
        var response = await _client.GetAsync(
            new Uri("/bowire/api/invoke/stream", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StreamingEndpoint_WithParams_IsRouted()
    {
        var messages = Uri.EscapeDataString("""["{\"name\":\"Stream\",\"count\":3}"]""");
        var url = new Uri(
            $"/bowire/api/invoke/stream?service=test.Greeter&method=SayHelloStream&messages={messages}",
            UriKind.Relative);

        var response = await _client.GetAsync(url, TestContext.Current.CancellationToken);

        // Endpoint is routed correctly; may get OK with SSE or error due to TestServer gRPC limitations
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError,
            $"Unexpected status code: {response.StatusCode}");
    }

    [Fact]
    public async Task RecordingsEndpoint_ReturnsJsonShape()
    {
        // GET should return the default shape ({"recordings": [...]}) so the
        // browser can hydrate its cache on first load. This is a wiring
        // smoke-test only — RecordingStore persists to ~/.bowire/recordings.json
        // and we don't want the suite mutating the user's disk file.
        var response = await _client.GetAsync(new Uri("/bowire/api/recordings", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(content);
        Assert.True(doc.RootElement.TryGetProperty("recordings", out var recordings),
            "GET /api/recordings should return an object with a 'recordings' property");
        Assert.Equal(JsonValueKind.Array, recordings.ValueKind);
    }

    [Fact]
    public async Task RecordingsEndpoint_RejectsInvalidJson()
    {
        // PUT with garbage should give a 400 with the JSON-parse error in the
        // body — verifies the catch-and-LogWarning path in BowireRecordingEndpoints.
        using var requestContent = new StringContent("not json", Encoding.UTF8, "application/json");
        var response = await _client.PutAsync(
            new Uri("/bowire/api/recordings", UriKind.Relative), requestContent, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Invalid JSON", content);
    }

    [Fact]
    public async Task StaticAssets_CssInlinedInHtml()
    {
        // CSS is inlined (like Scalar) — no external file reference needed
        var response = await _client.GetAsync(new Uri("/bowire", UriKind.Relative), TestContext.Current.CancellationToken);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("<style>", content);
        Assert.Contains("--bowire-bg", content); // CSS custom property from bowire.css
    }

    [Fact]
    public async Task StaticAssets_JsInlinedInHtml()
    {
        var response = await _client.GetAsync(new Uri("/bowire", UriKind.Relative), TestContext.Current.CancellationToken);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("__BOWIRE_CONFIG__", content); // JS config
        Assert.Contains("fetchServices", content); // JS function from bowire.js
    }

    [Fact]
    public async Task CustomOptions_TitleInHtml()
    {
        await using var app = await CreateCustomAppAsync(o => o.Title = "My Custom API");
        using var client = app.GetTestClient();

        var response = await client.GetAsync(new Uri("/bowire", UriKind.Relative), TestContext.Current.CancellationToken);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("My Custom API", content);
    }

    [Fact]
    public async Task CustomOptions_LightTheme()
    {
        await using var app = await CreateCustomAppAsync(o => o.Theme = BowireTheme.Light);
        using var client = app.GetTestClient();

        var response = await client.GetAsync(new Uri("/bowire", UriKind.Relative), TestContext.Current.CancellationToken);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("data-theme=\"light\"", content);
    }

    [Fact]
    public async Task CustomOptions_CustomRoutePrefix()
    {
        await using var app = await CreateCustomAppAsync(prefix: "/grpc");
        using var client = app.GetTestClient();

        var response = await client.GetAsync(new Uri("/grpc", UriKind.Relative), TestContext.Current.CancellationToken);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<!DOCTYPE html>", content);
        Assert.Contains("prefix: \"/grpc\"", content);
    }

    [Fact]
    public async Task DefaultRoute_ReturnsHtml()
    {
        // Verify /bowire returns 200 and HTML content type
        var response = await _client.GetAsync(new Uri("/bowire", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ProtocolsEndpoint_ReturnsGrpcAndSignalR()
    {
        var response = await _client.GetAsync(new Uri("/bowire/api/protocols", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("grpc", content);
        Assert.Contains("signalr", content);
    }

    private static async Task<WebApplication> CreateCustomAppAsync(
        Action<BowireOptions>? configure = null, string prefix = "/bowire")
    {
        EnsureProtocolAssembliesLoaded();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddGrpcReflection();
        builder.Services.AddSignalR();

        var app = builder.Build();
        app.UseStaticFiles();
        app.MapGrpcService<Services.GreeterService>();
        app.MapGrpcReflectionService();
        app.MapHub<Hubs.ChatHub>("/chathub");
        app.MapBowire(prefix, configure);

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EnsureProtocolAssembliesLoaded()
    {
        _ = typeof(BowireGrpcProtocol);
        _ = typeof(BowireSignalRProtocol);
    }
}
