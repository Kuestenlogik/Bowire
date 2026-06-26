// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Interceptor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Integration coverage for <c>BowireInterceptorEndpoints</c> (#153 + #308).
/// Pairs with the unit-suite <c>BowireInterceptorEndpointsTests</c> in
/// <c>Kuestenlogik.Bowire.Tests</c> — that file drives a Kestrel host
/// for the read paths; this file drives a TestServer for the mock-rule
/// mutation paths, the SSE stream, the "no store" 503 / 404 branches,
/// and the <c>POST /flows/{id}/mock</c> seed flow.
/// </summary>
public sealed class BowireInterceptorEndpointTests
{
    // ----- GET /api/intercepted/flows --------------------------------

    [Fact]
    public async Task GET_flows_without_store_returns_empty_array()
    {
        using var host = await BuildHost(includeStores: false);
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/intercepted/flows", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("flows").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("flows").GetArrayLength());
    }

    [Fact]
    public async Task GET_flows_returns_newest_first_snapshot()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var store = host.Services.GetRequiredService<InterceptedFlowStore>();
        store.Add(Flow(1));
        store.Add(Flow(2));
        store.Add(Flow(3));

        using var resp = await client.GetAsync(
            new Uri("/api/intercepted/flows", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var flows = doc.RootElement.GetProperty("flows");
        Assert.Equal(3, flows.GetArrayLength());
        Assert.Equal(3, flows[0].GetProperty("id").GetInt64());
        Assert.Equal(1, flows[2].GetProperty("id").GetInt64());
    }

    // ----- GET /api/intercepted/flows/{id} ---------------------------

    [Fact]
    public async Task GET_flow_by_id_without_store_returns_404()
    {
        using var host = await BuildHost(includeStores: false);
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/intercepted/flows/1", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GET_flow_by_id_returns_404_for_missing_id()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/intercepted/flows/9999", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GET_flow_by_id_returns_full_projection()
    {
        // Exercises the ProjectFull projection branch (request headers,
        // request body, response headers, response body, latency, etc.).
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var store = host.Services.GetRequiredService<InterceptedFlowStore>();
        store.Add(Flow(42));

        using var resp = await client.GetAsync(
            new Uri("/api/intercepted/flows/42", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("request-body",
            doc.RootElement.GetProperty("requestBody").GetString());
        Assert.Equal("""{"ok":true}""",
            doc.RootElement.GetProperty("responseBody").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("latencyMs").GetInt32());
        Assert.Equal(200, doc.RootElement.GetProperty("responseStatus").GetInt32());
    }

    [Fact]
    public async Task POST_recording_projects_flow_into_bowire_recording_shape()
    {
        // Exercises the ToRecording projection — same surface as the
        // proxy rail uses so the workbench's import path doesn't branch.
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var store = host.Services.GetRequiredService<InterceptedFlowStore>();
        store.Add(Flow(7, url: "https://api.example.com/v1/login", method: "POST", status: 201));

        using var emptyBody = new StringContent(string.Empty);
        using var resp = await client.PostAsync(
            new Uri("/api/intercepted/flows/7/recording", UriKind.Relative),
            emptyBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("intercepted-7", doc.RootElement.GetProperty("name").GetString());
        var step = doc.RootElement.GetProperty("steps")[0];
        Assert.Equal("POST", step.GetProperty("method").GetString());
        Assert.Equal("/v1/login", step.GetProperty("httpPath").GetString());
        Assert.Equal("https://api.example.com", step.GetProperty("serverUrl").GetString());
        Assert.Equal("201", step.GetProperty("status").GetString());
    }

    [Fact]
    public async Task POST_recording_for_failed_flow_records_error_status()
    {
        // Flow.ResponseStatus == 0 hits the "Error" branch in ToRecording.
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var store = host.Services.GetRequiredService<InterceptedFlowStore>();
        store.Add(Flow(11, url: "https://api.example.com/oops", status: 0));

        using var emptyBody = new StringContent(string.Empty);
        using var resp = await client.PostAsync(
            new Uri("/api/intercepted/flows/11/recording", UriKind.Relative),
            emptyBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Error",
            doc.RootElement.GetProperty("steps")[0].GetProperty("status").GetString());
    }

    // ----- DELETE /api/intercepted/flows -----------------------------

    [Fact]
    public async Task DELETE_flows_clears_store()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var store = host.Services.GetRequiredService<InterceptedFlowStore>();
        store.Add(Flow(1));

        using var resp = await client.DeleteAsync(
            new Uri("/api/intercepted/flows", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public async Task DELETE_flows_without_store_returns_204()
    {
        // The endpoint tolerates a missing store on the clear path —
        // mirrors the workbench's "Clear all" button always succeeding.
        using var host = await BuildHost(includeStores: false);
        var client = host.GetTestClient();

        using var resp = await client.DeleteAsync(
            new Uri("/api/intercepted/flows", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    // ----- POST /api/intercepted/flows/{id}/recording -----------------

    [Fact]
    public async Task POST_recording_returns_404_for_missing_flow()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var emptyBody = new StringContent(string.Empty);
        using var resp = await client.PostAsync(
            new Uri("/api/intercepted/flows/9999/recording", UriKind.Relative),
            emptyBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task POST_recording_without_store_returns_404()
    {
        using var host = await BuildHost(includeStores: false);
        var client = host.GetTestClient();

        using var emptyBody = new StringContent(string.Empty);
        using var resp = await client.PostAsync(
            new Uri("/api/intercepted/flows/1/recording", UriKind.Relative),
            emptyBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ----- GET /api/intercepted/mocks --------------------------------

    [Fact]
    public async Task GET_mocks_returns_master_toggle_and_rules()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockStore = host.Services.GetRequiredService<InterceptorMockStore>();
        mockStore.Add(new InterceptorMockRule
        {
            Name = "demo",
            PathPattern = "/api/users",
            Method = "GET",
            ResponseStatus = 200,
        });

        using var resp = await client.GetAsync(
            new Uri("/api/intercepted/mocks", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("enabled").GetBoolean());
        var rules = doc.RootElement.GetProperty("rules");
        Assert.Equal(1, rules.GetArrayLength());
        Assert.Equal("/api/users", rules[0].GetProperty("pathPattern").GetString());
    }

    // ----- POST /api/intercepted/mocks -------------------------------

    [Fact]
    public async Task POST_mock_creates_rule_and_returns_persisted_shape()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var body = JsonContent.Create(new
        {
            name = "demo-rule",
            pathPattern = "/api/v1/users",
            method = "GET",
            responseStatus = 201,
            responseBody = """{"hello":"world"}""",
        });
        using var resp = await client.PostAsync(
            new Uri("/api/intercepted/mocks", UriKind.Relative),
            body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var responseBody = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(responseBody);
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("id").GetString()));
        Assert.Equal("demo-rule", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(201, doc.RootElement.GetProperty("responseStatus").GetInt32());

        var store = host.Services.GetRequiredService<InterceptorMockStore>();
        Assert.Single(store.Snapshot());
    }

    [Fact]
    public async Task POST_mock_with_invalid_json_returns_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var body = new StringContent("{ not json", Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(
            new Uri("/api/intercepted/mocks", UriKind.Relative),
            body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task POST_mock_with_null_body_returns_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var body = new StringContent("null", Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(
            new Uri("/api/intercepted/mocks", UriKind.Relative),
            body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task POST_mock_without_store_returns_503()
    {
        using var host = await BuildHost(includeStores: false);
        var client = host.GetTestClient();

        using var body = JsonContent.Create(new { name = "x" });
        using var resp = await client.PostAsync(
            new Uri("/api/intercepted/mocks", UriKind.Relative),
            body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    // ----- DELETE /api/intercepted/mocks/{id} ------------------------

    [Fact]
    public async Task DELETE_mock_by_id_removes_rule()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockStore = host.Services.GetRequiredService<InterceptorMockStore>();
        var rule = mockStore.Add(new InterceptorMockRule { Name = "del-me", PathPattern = "/x" });

        using var resp = await client.DeleteAsync(
            new Uri($"/api/intercepted/mocks/{rule.Id}", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Empty(mockStore.Snapshot());
    }

    [Fact]
    public async Task DELETE_mock_by_id_unknown_returns_404()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.DeleteAsync(
            new Uri("/api/intercepted/mocks/does-not-exist", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task DELETE_mock_without_store_returns_404()
    {
        using var host = await BuildHost(includeStores: false);
        var client = host.GetTestClient();

        using var resp = await client.DeleteAsync(
            new Uri("/api/intercepted/mocks/anything", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task DELETE_all_mocks_clears_store()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockStore = host.Services.GetRequiredService<InterceptorMockStore>();
        mockStore.Add(new InterceptorMockRule { Name = "a" });
        mockStore.Add(new InterceptorMockRule { Name = "b" });

        using var resp = await client.DeleteAsync(
            new Uri("/api/intercepted/mocks", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Empty(mockStore.Snapshot());
    }

    // ----- PUT /api/intercepted/mocks/enabled ------------------------

    [Fact]
    public async Task PUT_mocks_enabled_flips_master_toggle()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var body = JsonContent.Create(new { enabled = false });
        using var resp = await client.PutAsync(
            new Uri("/api/intercepted/mocks/enabled", UriKind.Relative),
            body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var responseBody = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(responseBody);
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());

        // GET /mocks must now report the new toggle state.
        using var get = await client.GetAsync(
            new Uri("/api/intercepted/mocks", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var getBody = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var getDoc = JsonDocument.Parse(getBody);
        Assert.False(getDoc.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task PUT_mocks_enabled_with_invalid_json_returns_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var body = new StringContent("{ not json", Encoding.UTF8, "application/json");
        using var resp = await client.PutAsync(
            new Uri("/api/intercepted/mocks/enabled", UriKind.Relative),
            body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PUT_mocks_enabled_writes_default_options_when_unconfigured()
    {
        // The endpoint reads IOptions<BowireInterceptorOptions> via DI;
        // ASP.NET's default options machinery instantiates one even
        // without an explicit AddOptions() call, so the toggle write
        // succeeds against the default instance.
        using var host = await BuildHost(includeStores: false);
        var client = host.GetTestClient();

        using var body = JsonContent.Create(new { enabled = false });
        using var resp = await client.PutAsync(
            new Uri("/api/intercepted/mocks/enabled", UriKind.Relative),
            body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var responseBody = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(responseBody);
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
    }

    // ----- POST /api/intercepted/flows/{id}/mock ---------------------

    [Fact]
    public async Task POST_seed_mock_creates_rule_from_flow()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var flowStore = host.Services.GetRequiredService<InterceptedFlowStore>();
        var mockStore = host.Services.GetRequiredService<InterceptorMockStore>();
        flowStore.Add(Flow(99, url: "https://api.example.com/v1/widgets?id=42", method: "POST", status: 201));

        using var emptyBody = new StringContent(string.Empty);
        using var resp = await client.PostAsync(
            new Uri("/api/intercepted/flows/99/mock", UriKind.Relative),
            emptyBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("mock-of-99", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("POST", doc.RootElement.GetProperty("method").GetString());
        Assert.Equal(201, doc.RootElement.GetProperty("responseStatus").GetInt32());
        Assert.Single(mockStore.Snapshot());
    }

    [Fact]
    public async Task POST_seed_mock_unknown_flow_returns_404()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var emptyBody = new StringContent(string.Empty);
        using var resp = await client.PostAsync(
            new Uri("/api/intercepted/flows/9999/mock", UriKind.Relative),
            emptyBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task POST_seed_mock_without_store_returns_404()
    {
        using var host = await BuildHost(includeStores: false);
        var client = host.GetTestClient();

        using var emptyBody = new StringContent(string.Empty);
        using var resp = await client.PostAsync(
            new Uri("/api/intercepted/flows/1/mock", UriKind.Relative),
            emptyBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task POST_seed_mock_with_overrides_applies_them()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var flowStore = host.Services.GetRequiredService<InterceptedFlowStore>();
        flowStore.Add(Flow(5));

        using var body = JsonContent.Create(new
        {
            name = "custom",
            pathPattern = "/override/path",
            method = "DELETE",
        });
        using var resp = await client.PostAsync(
            new Uri("/api/intercepted/flows/5/mock", UriKind.Relative),
            body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var responseBody = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(responseBody);
        Assert.Equal("custom", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("/override/path", doc.RootElement.GetProperty("pathPattern").GetString());
        Assert.Equal("DELETE", doc.RootElement.GetProperty("method").GetString());
    }

    // ----- GET /api/intercepted/stream -------------------------------

    [Fact]
    public async Task GET_stream_without_store_returns_404()
    {
        using var host = await BuildHost(includeStores: false);
        var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Get,
            new Uri("/api/intercepted/stream", UriKind.Relative));
        using var resp = await client.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GET_stream_returns_event_stream_content_type()
    {
        // Just probes the SSE seam — header content-type + 200. The
        // streaming body is asserted by the Kestrel-backed unit suite
        // in Kuestenlogik.Bowire.Tests; TestServer's response buffering
        // makes a "wait for an event" assertion brittle on this host.
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Get,
            new Uri("/api/intercepted/stream", UriKind.Relative));
        // Cap the read to a few hundred ms — the endpoint loops forever
        // by design; we only need to verify the response shape.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            using var resp = await client.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        }
        catch (OperationCanceledException) { /* expected — we cancel to bail out */ }
    }

    // ----- Host builder ----------------------------------------------

    private static InterceptedFlow Flow(long id, string url = "http://example/foo", string method = "GET", int status = 200) => new()
    {
        Id = id,
        CapturedAt = DateTimeOffset.UtcNow,
        Method = method,
        Url = url,
        Path = "/foo",
        Scheme = url.StartsWith("https://", StringComparison.Ordinal) ? "https" : "http",
        RequestHeaders = new[] { new KeyValuePair<string, string>("Host", "example") },
        RequestBody = "request-body",
        ResponseStatus = status,
        ResponseHeaders = new[] { new KeyValuePair<string, string>("Content-Type", "application/json") },
        ResponseBody = """{"ok":true}""",
        LatencyMs = 42,
    };

    private static async Task<IHost> BuildHost(bool includeStores = true)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireInterceptorEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       if (includeStores)
                       {
                           s.AddBowireInterceptorCore();
                       }
                   });
            })
            .Build();
        await host.StartAsync();
        return host;
    }
}
