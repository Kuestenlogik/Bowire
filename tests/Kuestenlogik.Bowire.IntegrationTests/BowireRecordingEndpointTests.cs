// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Integration coverage for <c>BowireRecordingEndpoints</c> (#144 / #282).
/// Drives the disk-backed recording surface (<c>GET / PUT / DELETE</c>
/// list + per-step manifest / load / append) via a TestServer with
/// <see cref="BowireUserContext"/> redirected to a per-test temp root.
/// </summary>
[Collection("BowireUserContext")]
public sealed class BowireRecordingEndpointTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _tempRoot;

    public BowireRecordingEndpointTests()
    {
        _originalStore = BowireUserContext.Current;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"bowire-recordings-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        BowireUserContext.Current = new TempStore(_tempRoot);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _originalStore;
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ----- GET /api/recordings ---------------------------------------

    [Fact]
    public async Task GET_returns_empty_recordings_envelope_when_no_disk_state()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/recordings?workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("recordings", out var arr));
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(0, arr.GetArrayLength());
    }

    [Fact]
    public async Task GET_manifestOnly_returns_envelope()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/recordings?workspaceId=ws-1&manifestOnly=1", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("recordings", out _));
    }

    // ----- PUT /api/recordings ---------------------------------------

    [Fact]
    public async Task PUT_then_GET_round_trips_recording()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        const string payload = """
        {
          "recordings": [
            {
              "id": "rec-1",
              "name": "first",
              "createdAt": 0,
              "steps": [
                {"id":"s1","protocol":"rest","service":"x","method":"M","httpVerb":"GET","httpPath":"/"}
              ]
            }
          ]
        }
        """;
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var put = await client.PutAsync(
            new Uri("/api/recordings?workspaceId=ws-1", UriKind.Relative),
            content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var putBody = await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var putDoc = JsonDocument.Parse(putBody);
        Assert.True(putDoc.RootElement.GetProperty("saved").GetBoolean());

        using var get = await client.GetAsync(
            new Uri("/api/recordings?workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("recordings").GetArrayLength());
        Assert.Equal("first",
            doc.RootElement.GetProperty("recordings")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task PUT_invalid_json_returns_400_problem_details()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");
        using var resp = await client.PutAsync(
            new Uri("/api/recordings?workspaceId=ws-1", UriKind.Relative),
            content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("urn:bowire:invalid-input",
            doc.RootElement.GetProperty("type").GetString());
    }

    // ----- DELETE /api/recordings ------------------------------------

    [Fact]
    public async Task DELETE_clears_recordings_for_workspace()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var seedContent = new StringContent(
            """{"recordings":[{"id":"rec-1","name":"x","createdAt":0,"steps":[]}]}""",
            Encoding.UTF8, "application/json");
        using var seed = await client.PutAsync(
            new Uri("/api/recordings?workspaceId=ws-1", UriKind.Relative),
            seedContent, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        using var del = await client.DeleteAsync(
            new Uri("/api/recordings?workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var delBody = await del.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var delDoc = JsonDocument.Parse(delBody);
        Assert.True(delDoc.RootElement.GetProperty("cleared").GetBoolean());

        using var get = await client.GetAsync(
            new Uri("/api/recordings?workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var body = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("recordings").GetArrayLength());
    }

    // ----- GET /api/recordings/{id}/manifest --------------------------

    [Fact]
    public async Task GET_manifest_unknown_id_returns_404_problem()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/recordings/does-not-exist/manifest?workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("urn:bowire:recording-not-found",
            doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task GET_manifest_returns_known_recording()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        // Seed via PUT so the chunked layout is initialised.
        using var seedContent = new StringContent(
            """{"recordings":[{"id":"rec-7","name":"manifest-target","createdAt":0,"steps":[{"id":"s1","protocol":"rest","method":"M","httpVerb":"GET","httpPath":"/"}]}]}""",
            Encoding.UTF8, "application/json");
        using var seed = await client.PutAsync(
            new Uri("/api/recordings?workspaceId=ws-1", UriKind.Relative),
            seedContent, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        using var resp = await client.GetAsync(
            new Uri("/api/recordings/rec-7/manifest?workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        // Manifest carries the recording id + metadata; the exact key
        // name comes from ChunkedRecordingStore — pin the id so a future
        // shape change breaks here.
        Assert.Contains("rec-7", body, StringComparison.Ordinal);
        _ = doc; // keep doc alive for parse-side effect
    }

    // ----- GET /api/recordings/{id}/step/{n} -------------------------

    [Fact]
    public async Task GET_step_unknown_returns_404_problem()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/recordings/no-such/step/0?workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("urn:bowire:step-not-found",
            doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task GET_step_returns_known_step_body()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var seedContent = new StringContent(
            """{"recordings":[{"id":"rec-step","name":"sx","createdAt":0,"steps":[{"id":"s1","protocol":"rest","method":"M","httpVerb":"GET","httpPath":"/"}]}]}""",
            Encoding.UTF8, "application/json");
        using var seed = await client.PutAsync(
            new Uri("/api/recordings?workspaceId=ws-1", UriKind.Relative),
            seedContent, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        using var resp = await client.GetAsync(
            new Uri("/api/recordings/rec-step/step/0?workspaceId=ws-1", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("s1", body, StringComparison.Ordinal);
    }

    // ----- POST /api/recordings/{id}/step ----------------------------

    [Fact]
    public async Task POST_step_appends_to_existing_recording()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        // Seed a recording with no steps first.
        using var seedContent = new StringContent(
            """{"recordings":[{"id":"rec-app","name":"appendable","createdAt":0,"steps":[]}]}""",
            Encoding.UTF8, "application/json");
        using var seed = await client.PutAsync(
            new Uri("/api/recordings?workspaceId=ws-1", UriKind.Relative),
            seedContent, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        using var stepBody = JsonContent.Create(new
        {
            step = new
            {
                id = "appended-step",
                protocol = "rest",
                method = "M",
                httpVerb = "POST",
                httpPath = "/x"
            }
        });
        using var resp = await client.PostAsync(
            new Uri("/api/recordings/rec-app/step?workspaceId=ws-1", UriKind.Relative),
            stepBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("appended").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("stepIndex").GetInt32());
    }

    [Fact]
    public async Task POST_step_accepts_bare_step_object()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        // Seed.
        using var seedContent = new StringContent(
            """{"recordings":[{"id":"rec-bare","name":"bare","createdAt":0,"steps":[]}]}""",
            Encoding.UTF8, "application/json");
        using var seed = await client.PutAsync(
            new Uri("/api/recordings?workspaceId=ws-1", UriKind.Relative),
            seedContent, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        // Bare-step shape (no envelope) — the endpoint accepts this for
        // lenient backwards compat.
        using var bareBody = JsonContent.Create(new
        {
            id = "bare-step",
            protocol = "rest",
            method = "M",
            httpVerb = "GET",
            httpPath = "/"
        });
        using var resp = await client.PostAsync(
            new Uri("/api/recordings/rec-bare/step?workspaceId=ws-1", UriKind.Relative),
            bareBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("appended").GetBoolean());
    }

    [Fact]
    public async Task POST_step_invalid_json_returns_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(
            new Uri("/api/recordings/anything/step?workspaceId=ws-1", UriKind.Relative),
            content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("urn:bowire:invalid-input",
            doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task POST_step_non_object_body_returns_400()
    {
        // A JSON array at the root trips the "must be a JSON object" guard.
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var content = new StringContent("[1, 2, 3]", Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(
            new Uri("/api/recordings/anything/step?workspaceId=ws-1", UriKind.Relative),
            content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static async Task<IHost> BuildHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireRecordingEndpoints(
                           new BowireOptions(), basePath: string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    private sealed class TempStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }
}
