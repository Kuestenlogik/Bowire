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

    // ---------- BowireUploadEndpoints ----------

    [Fact]
    public async Task ProtoUpload_EmptyBody_Returns400()
    {
        using var content = new StringContent("", Encoding.UTF8, "text/plain");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/proto/upload", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("proto", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProtoUpload_ValidContent_ReturnsImportedCount()
    {
        // Tiny proto with one service so the parser returns >0 services.
        const string Proto = """
            syntax = "proto3";
            package demo;
            service Pinger { rpc Ping (Empty) returns (Empty); }
            message Empty {}
            """;
        using var content = new StringContent(Proto, Encoding.UTF8, "text/plain");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/proto/upload", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("imported", out var imported));
        Assert.True(imported.GetInt32() >= 1);
    }

    [Fact]
    public async Task ProtoUpload_Delete_ClearsStore()
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete,
            new Uri("/bowire/api/proto/upload", UriKind.Relative));

        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"cleared\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApiUpload_EmptyBody_Returns400()
    {
        using var content = new StringContent("", Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/openapi/upload", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("OpenAPI", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenApiUpload_WithName_Query_ReturnsId()
    {
        // Minimal OpenAPI 3 doc — body content isn't validated by the
        // store, the REST plugin parses it lazily on first discovery.
        const string Spec = """{"openapi":"3.0.0","info":{"title":"x","version":"1"}}""";
        using var content = new StringContent(Spec, Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/openapi/upload?name=demo.json", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("uploaded").GetBoolean());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task OpenApiUpload_Delete_ClearsStore()
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete,
            new Uri("/bowire/api/openapi/upload", UriKind.Relative));

        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ---------- BowireWorkspaceEndpoints ----------

    [Fact]
    public async Task Workspace_Get_NoFile_ReturnsEmptyDefaults()
    {
        // The fixture's working directory typically has no .blw — Get
        // should respond with the empty WorkspaceFile shape rather than
        // 404'ing or throwing.
        var resp = await _client.GetAsync(
            new Uri("/bowire/api/workspace", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        // Properties exist (camel-cased by JsonSerializer default for records)
        Assert.True(doc.RootElement.TryGetProperty("urls", out var urls)
            || doc.RootElement.TryGetProperty("Urls", out urls));
        Assert.Equal(JsonValueKind.Array, urls.ValueKind);
    }

    [Fact]
    public async Task Workspace_Put_InvalidJson_Returns400()
    {
        using var req = new HttpRequestMessage(HttpMethod.Put,
            new Uri("/bowire/api/workspace", UriKind.Relative))
        {
            Content = new StringContent("{ broken", Encoding.UTF8, "application/json")
        };

        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---------- BowireEnvironmentEndpoints ----------

    [Fact]
    public async Task Environments_Get_ReturnsJsonShape()
    {
        var resp = await _client.GetAsync(
            new Uri("/bowire/api/environments", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        // EnvironmentStore.Load contract: envelope with globals + environments.
        Assert.True(doc.RootElement.TryGetProperty("globals", out _));
        Assert.True(doc.RootElement.TryGetProperty("environments", out _));
    }

    [Fact]
    public async Task Environments_Put_InvalidJson_Returns400()
    {
        using var req = new HttpRequestMessage(HttpMethod.Put,
            new Uri("/bowire/api/environments", UriKind.Relative))
        {
            Content = new StringContent("{ broken", Encoding.UTF8, "application/json")
        };

        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Invalid JSON", json, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- BowireChannelEndpoints — happy paths via FakeChannelProtocol ----------
    //
    // The Fakes.FakeChannelProtocol is auto-discovered by
    // BowireProtocolRegistry.Discover() because the IntegrationTests
    // assembly name contains "Bowire". Its OpenChannelAsync returns an
    // in-memory IBowireChannel that echoes every send back through the
    // response channel and completes cleanly on close. That covers the
    // open / send / close / SSE branches without needing a real
    // gRPC or WebSocket listener.

    private static async Task<string> OpenFakeChannelAsync(HttpClient client, CancellationToken ct)
    {
        var openBody = """{"protocol":"fake","service":"echo.Service","method":"Echo"}""";
        using var openContent = new StringContent(openBody, Encoding.UTF8, "application/json");
        var openResp = await client.PostAsync(
            new Uri("/bowire/api/channel/open", UriKind.Relative), openContent, ct);
        Assert.Equal(HttpStatusCode.OK, openResp.StatusCode);
        var openJson = await openResp.Content.ReadAsStringAsync(ct);
        using var openDoc = JsonDocument.Parse(openJson);
        return openDoc.RootElement.GetProperty("channelId").GetString()!;
    }

    [Fact]
    public async Task ChannelOpen_FakeProtocol_Returns200WithChannelId()
    {
        var ct = TestContext.Current.CancellationToken;
        var openBody = """{"protocol":"fake","service":"echo.Service","method":"Echo"}""";
        using var content = new StringContent(openBody, Encoding.UTF8, "application/json");

        var resp = await _client.PostAsync(
            new Uri("/bowire/api/channel/open", UriKind.Relative), content, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        // FakeChannel sets both streaming flags to true so the response
        // exercises the negotiated-channel branch in the open handler.
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("channelId").GetString()));
        Assert.True(doc.RootElement.GetProperty("clientStreaming").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("serverStreaming").GetBoolean());
    }

    [Fact]
    public async Task ChannelSend_OpenChannel_ReturnsSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        var channelId = await OpenFakeChannelAsync(_client, ct);

        // Two sends — second response should report sequence == 2.
        using var send1 = new StringContent("""{"message":"\"hello\""}""",
            Encoding.UTF8, "application/json");
        var resp1 = await _client.PostAsync(
            new Uri($"/bowire/api/channel/{channelId}/send", UriKind.Relative), send1, ct);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        using var send2 = new StringContent("""{"message":"\"world\""}""",
            Encoding.UTF8, "application/json");
        var resp2 = await _client.PostAsync(
            new Uri($"/bowire/api/channel/{channelId}/send", UriKind.Relative), send2, ct);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var json2 = await resp2.Content.ReadAsStringAsync(ct);
        using var doc2 = JsonDocument.Parse(json2);
        Assert.True(doc2.RootElement.GetProperty("sent").GetBoolean());
        Assert.Equal(2, doc2.RootElement.GetProperty("sequence").GetInt32());
    }

    [Fact]
    public async Task ChannelClose_OpenChannel_ReturnsClosedTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var channelId = await OpenFakeChannelAsync(_client, ct);

        using var send = new StringContent("""{"message":"\"once\""}""",
            Encoding.UTF8, "application/json");
        await _client.PostAsync(
            new Uri($"/bowire/api/channel/{channelId}/send", UriKind.Relative), send, ct);

        using var closeContent = new StringContent("", Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync(
            new Uri($"/bowire/api/channel/{channelId}/close", UriKind.Relative), closeContent, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("closed").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("sentCount").GetInt32());
    }

    [Fact]
    public async Task ChannelSend_AfterClose_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var channelId = await OpenFakeChannelAsync(_client, ct);

        using var closeContent = new StringContent("", Encoding.UTF8, "application/json");
        await _client.PostAsync(
            new Uri($"/bowire/api/channel/{channelId}/close", UriKind.Relative), closeContent, ct);

        // After close, send should hit the IsClosed branch and return 400.
        using var send = new StringContent("""{"message":"\"late\""}""",
            Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync(
            new Uri($"/bowire/api/channel/{channelId}/send", UriKind.Relative), send, ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ChannelResponses_AfterSendAndClose_StreamsEventsAndDone()
    {
        var ct = TestContext.Current.CancellationToken;
        var channelId = await OpenFakeChannelAsync(_client, ct);

        // Send + close BEFORE we connect to /responses so the SSE handler
        // can drain the queue and emit `event: done` immediately. This
        // makes the test deterministic — no race between SSE reader and
        // the send/close pump.
        using var send = new StringContent("""{"message":"\"ping\""}""",
            Encoding.UTF8, "application/json");
        await _client.PostAsync(
            new Uri($"/bowire/api/channel/{channelId}/send", UriKind.Relative), send, ct);
        using var closeContent = new StringContent("", Encoding.UTF8, "application/json");
        await _client.PostAsync(
            new Uri($"/bowire/api/channel/{channelId}/close", UriKind.Relative), closeContent, ct);

        var resp = await _client.GetAsync(
            new Uri($"/bowire/api/channel/{channelId}/responses", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        // Each `data: <json>` line carries `{"index":0,"data":...,"timestampMs":...}`.
        // The fake echoes back the input JSON wrapped in `{"echo": ...}`,
        // which the SSE handler re-encodes as a string field — so the
        // payload arrives as `"data":"{\"echo\":\"ping\"}"` over the wire.
        Assert.Contains("echo", body, StringComparison.Ordinal);
        Assert.Contains("\"index\":0", body, StringComparison.Ordinal);
        // Channel completed → `event: done` line with sentCount/durationMs.
        Assert.Contains("event: done", body, StringComparison.Ordinal);
        Assert.Contains("\"sentCount\":1", body, StringComparison.Ordinal);
    }
}
