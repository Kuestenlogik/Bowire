// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Integration coverage for <c>BowireMockConfigEndpoints</c> (#558). Drives
/// <c>GET</c> and <c>PUT /api/mocks/{id}/config</c> through a TestServer with
/// <see cref="BowireUserContext"/> redirected to a per-test temp root, so
/// the round-trip through <c>MockConfigStore</c> is exercised without
/// touching the developer's real <c>~/.bowire/</c>. Mirrors
/// <see cref="BowirePresetEndpointTests"/>.
/// </summary>
[Collection("BowireUserContext")]
public sealed class BowireMockConfigEndpointTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _tempRoot;

    public BowireMockConfigEndpointTests()
    {
        _originalStore = BowireUserContext.Current;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"bowire-mockcfg-ep-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        BowireUserContext.Current = new TempStore(_tempRoot);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _originalStore;
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GET_no_file_returns_default_envelope()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/mocks/m1/config?workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("configFormatVersion", out _));
    }

    [Fact]
    public async Task PUT_then_GET_round_trips_config_payload()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        const string payload = """
        {"configFormatVersion":1,"fieldOverrides":[{"service":"Orders","method":"list","jsonPath":"$.total","value":42}]}
        """;
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var put = await client.PutAsync(
            new Uri("/api/mocks/m1/config?workspaceId=ws-1", UriKind.Relative),
            content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var get = await client.GetAsync(
            new Uri("/api/mocks/m1/config?workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var body = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var ov = doc.RootElement.GetProperty("fieldOverrides")[0];
        Assert.Equal("Orders", ov.GetProperty("service").GetString());
        Assert.Equal(42, ov.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task PUT_invalid_json_returns_400_with_error_body()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");
        using var resp = await client.PutAsync(
            new Uri("/api/mocks/m1/config?workspaceId=ws-1", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Invalid JSON", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PUT_non_object_json_returns_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var content = new StringContent("[1,2,3]", Encoding.UTF8, "application/json");
        using var resp = await client.PutAsync(
            new Uri("/api/mocks/m1/config?workspaceId=ws-1", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PUT_empty_body_returns_400()
    {
        // An empty body → MockConfigStore.Save throws ArgumentException
        // ("JSON payload required") → the PUT ArgumentException→400 branch.
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var content = new StringContent("", Encoding.UTF8, "application/json");
        using var resp = await client.PutAsync(
            new Uri("/api/mocks/m1/config?workspaceId=ws-1", UriKind.Relative),
            content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GET_unsafe_mock_id_returns_400()
    {
        // An all-unsafe mockId fails MockConfigStore's path-safety sanitiser
        // (ArgumentException, thrown from GetStorePath outside Load's try) →
        // the GET ArgumentException→400 branch.
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/mocks/!!!/config?workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Configs_isolated_per_mock()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var aContent = new StringContent(
            """{"source":{"kind":"openapi","path":"a"}}""", Encoding.UTF8, "application/json");
        using var aResp = await client.PutAsync(
            new Uri("/api/mocks/mock-a/config?workspaceId=ws-1", UriKind.Relative),
            aContent, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, aResp.StatusCode);

        // A different mock id sees the default envelope, not mock-a's config.
        using var getB = await client.GetAsync(
            new Uri("/api/mocks/mock-b/config?workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var bBody = await getB.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("openapi", bBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configs_isolated_per_workspace()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var aContent = new StringContent(
            """{"source":{"kind":"graphql","path":"a"}}""", Encoding.UTF8, "application/json");
        using var aResp = await client.PutAsync(
            new Uri("/api/mocks/m1/config?workspaceId=ws-a", UriKind.Relative),
            aContent, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, aResp.StatusCode);

        using var getB = await client.GetAsync(
            new Uri("/api/mocks/m1/config?workspaceId=ws-b", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var bBody = await getB.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("graphql", bBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GET_auth_recordings_lists_credential_free_summaries()
    {
        // #563: seed two recordings into the temp workspace store, then list them.
        AuthRecordingStore.Save("ws-1", null, new AuthRecording { Id = "login", Name = "Login", Scheme = "bearer", Credential = "super-secret" });
        AuthRecordingStore.Save("ws-1", null, new AuthRecording { Id = "apikey", Name = "API key", Scheme = "apikey", Credential = "k-123" });

        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/auth-recordings?workspaceId=ws-1", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // The listing carries ids/names but NEVER the credential value.
        Assert.DoesNotContain("super-secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("k-123", body, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(body);
        var ids = doc.RootElement.GetProperty("recordings").EnumerateArray()
            .Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.Contains("login", ids);
        Assert.Contains("apikey", ids);
    }

    [Fact]
    public async Task GET_auth_recordings_empty_workspace_returns_empty_list()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/auth-recordings?workspaceId=nobody", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Empty(doc.RootElement.GetProperty("recordings").EnumerateArray());
    }

    [Fact]
    public async Task PUT_auth_recording_then_GET_lists_it_without_the_credential()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        const string body = """{"id":"login","name":"Login","scheme":"bearer","credential":"tok-xyz"}""";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var put = await client.PutAsync(
            new Uri("/api/auth-recordings/login?workspaceId=ws-1", UriKind.Relative), content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var get = await client.GetAsync(
            new Uri("/api/auth-recordings?workspaceId=ws-1", UriKind.Relative), TestContext.Current.CancellationToken);
        var listing = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("tok-xyz", listing, StringComparison.Ordinal);   // credential never leaves the store
        using var doc = JsonDocument.Parse(listing);
        var ids = doc.RootElement.GetProperty("recordings").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.Contains("login", ids);
    }

    [Fact]
    public async Task PUT_auth_recording_the_url_owns_the_id()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        // The body carries a different id; the URL id wins so the filename and
        // the stored id stay consistent.
        const string body = """{"id":"ignored","credential":"tok"}""";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var put = await client.PutAsync(
            new Uri("/api/auth-recordings/real-id?workspaceId=ws-1", UriKind.Relative), content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var get = await client.GetAsync(
            new Uri("/api/auth-recordings?workspaceId=ws-1", UriKind.Relative), TestContext.Current.CancellationToken);
        var listing = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("real-id", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PUT_auth_recording_without_a_credential_returns_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var content = new StringContent("""{"scheme":"bearer"}""", Encoding.UTF8, "application/json");
        using var put = await client.PutAsync(
            new Uri("/api/auth-recordings/x?workspaceId=ws-1", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task DELETE_auth_recording_removes_it()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using (var content = new StringContent("""{"credential":"tok"}""", Encoding.UTF8, "application/json"))
        {
            using var put = await client.PutAsync(
                new Uri("/api/auth-recordings/gone?workspaceId=ws-1", UriKind.Relative), content, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        using var del = await client.DeleteAsync(
            new Uri("/api/auth-recordings/gone?workspaceId=ws-1", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var delBody = await del.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using (var doc = JsonDocument.Parse(delBody))
        {
            Assert.True(doc.RootElement.GetProperty("deleted").GetBoolean());
        }

        using var get = await client.GetAsync(
            new Uri("/api/auth-recordings?workspaceId=ws-1", UriKind.Relative), TestContext.Current.CancellationToken);
        var listing = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var listDoc = JsonDocument.Parse(listing);
        Assert.Empty(listDoc.RootElement.GetProperty("recordings").EnumerateArray());
    }

    [Fact]
    public async Task POST_capture_with_a_flow_capturer_stores_the_credential()
    {
        using var host = await BuildHost(new FakeCapturer("flow-tok", "bearer", null));
        var client = host.GetTestClient();

        using var content = new StringContent("""{"steps":[]}""", Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(
            new Uri("/api/auth-recordings/login/capture?workspaceId=ws-1", UriKind.Relative), content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var get = await client.GetAsync(
            new Uri("/api/auth-recordings?workspaceId=ws-1", UriKind.Relative), TestContext.Current.CancellationToken);
        var listing = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("flow-tok", listing, StringComparison.Ordinal);   // captured credential never listed
        Assert.Contains("login", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task POST_capture_without_a_capturer_returns_501()
    {
        using var host = await BuildHost();   // no IAuthFlowCapturer registered
        var client = host.GetTestClient();

        using var content = new StringContent("""{"steps":[]}""", Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(
            new Uri("/api/auth-recordings/x/capture?workspaceId=ws-1", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
    }

    private static async Task<IHost> BuildHost(IAuthFlowCapturer? capturer = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireMockConfigEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       if (capturer is not null) s.AddSingleton(capturer);
                   });
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    private sealed class FakeCapturer(string credential, string? scheme, string? header) : IAuthFlowCapturer
    {
        public Task<AuthFlowCaptureResult> CaptureAsync(string flowJson, CancellationToken ct = default)
            => Task.FromResult(new AuthFlowCaptureResult(credential, scheme, header));
    }

    private sealed class TempStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }
}
