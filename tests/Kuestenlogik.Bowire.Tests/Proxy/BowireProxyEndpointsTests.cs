// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Proxy;

/// <summary>
/// Coverage for the workbench-facing proxy endpoints — the sidecar
/// API the Workbench's "Proxy" tab will consume. Spins up a Kestrel
/// app with <see cref="BowireProxyEndpoints"/> mounted on the empty
/// base path, seeds a <see cref="CapturedFlowStore"/> with synthetic
/// flows, then exercises every endpoint over real loopback HTTP.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class BowireProxyEndpointsTests
{
    private static CapturedFlow Flow(long id, string url = "http://example/foo", string method = "GET", int status = 200) => new()
    {
        Id = id,
        CapturedAt = DateTimeOffset.UtcNow,
        Method = method,
        Url = url,
        Scheme = url.StartsWith("https://", StringComparison.Ordinal) ? "https" : "http",
        RequestHeaders = new[] { new KeyValuePair<string, string>("Host", "example") },
        RequestBody = "request-body",
        ResponseStatus = status,
        ResponseHeaders = new[] { new KeyValuePair<string, string>("Content-Type", "application/json") },
        ResponseBody = "{\"ok\":true}",
        LatencyMs = 42,
    };

    private static async Task<(WebApplication app, HttpClient http, CapturedFlowStore store)> StartAsync(CancellationToken ct)
    {
        var store = new CapturedFlowStore();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.MapBowireProxyEndpoints("", store);
        await app.StartAsync(ct);
        var addr = app.Urls.First();
        var http = new HttpClient { BaseAddress = new Uri(addr) };
        return (app, http, store);
    }

    [Fact]
    public async Task GetFlows_ReturnsNewestFirstSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store) = await StartAsync(ct);
        await using var _ = app;
        using var __ = http;
        store.Add(Flow(1));
        store.Add(Flow(2));
        store.Add(Flow(3));

        using var resp = await http.GetAsync(new Uri("/api/proxy/flows", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var flows = payload.GetProperty("flows").EnumerateArray().ToArray();
        Assert.Equal(3, flows.Length);
        Assert.Equal(3, flows[0].GetProperty("id").GetInt64());
        Assert.Equal(1, flows[2].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task GetFlowById_ReturnsFullFlow_OrNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store) = await StartAsync(ct);
        await using var _ = app;
        using var __ = http;
        store.Add(Flow(42));

        using var hit = await http.GetAsync(new Uri("/api/proxy/flows/42", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
        var full = await hit.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("request-body", full.GetProperty("requestBody").GetString());
        Assert.Equal("{\"ok\":true}", full.GetProperty("responseBody").GetString());

        using var miss = await http.GetAsync(new Uri("/api/proxy/flows/9999", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);
    }

    [Fact]
    public async Task DeleteFlows_ClearsTheStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store) = await StartAsync(ct);
        await using var _ = app;
        using var __ = http;
        store.Add(Flow(1));
        store.Add(Flow(2));

        using var resp = await http.DeleteAsync(new Uri("/api/proxy/flows", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public async Task ProjectAsRecording_ReturnsBowireRecordingShape()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store) = await StartAsync(ct);
        await using var _ = app;
        using var __ = http;
        store.Add(Flow(7, url: "https://api.example.com/v1/login", method: "POST", status: 201));

        using var resp = await http.PostAsync(new Uri("/api/proxy/flows/7/recording", UriKind.Relative), content: null, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var rec = await resp.Content.ReadFromJsonAsync<BowireRecording>(ct);
        Assert.NotNull(rec);
        Assert.Equal("proxy-capture-7", rec!.Name);
        Assert.Single(rec.Steps);
        var step = rec.Steps[0];
        Assert.Equal("POST", step.Method);
        Assert.Equal("/v1/login", step.HttpPath);
        Assert.Equal("https://api.example.com", step.ServerUrl);
        Assert.Equal("201", step.Status);
    }

    [Fact]
    public async Task ProjectAsRecording_UnknownId_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, _) = await StartAsync(ct);
        await using var _app = app;
        using var __ = http;
        using var resp = await http.PostAsync(new Uri("/api/proxy/flows/12345/recording", UriKind.Relative), content: null, ct);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // SSE endpoint registration is covered transitively by
    // CapturedFlowStoreTests.Subscribe_* (the underlying mechanism).
    // A direct HTTP probe of /api/proxy/stream is omitted here because
    // the open-ended event stream interacts badly with the in-process
    // test fixture's lifecycle (connection cleanup races Kestrel
    // shutdown). The Workbench tab integration test in Stage C will
    // cover the wire-level contract end-to-end.


    [Fact]
    public async Task FlowWithBinaryBody_RoundTripsBase64InFullView()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store) = await StartAsync(ct);
        await using var _ = app;
        using var __ = http;
        store.Add(new CapturedFlow
        {
            Id = 200,
            CapturedAt = DateTimeOffset.UtcNow,
            Method = "POST",
            Url = "http://example/upload",
            RequestBodyBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 0, 4, 5 }),
            ResponseStatus = 200,
            ResponseBodyBase64 = Convert.ToBase64String(new byte[] { 6, 7, 8 }),
            LatencyMs = 12,
        });

        using var resp = await http.GetAsync(new Uri("/api/proxy/flows/200", UriKind.Relative), ct);
        var full = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("AQIDAAQF", full.GetProperty("requestBodyBase64").GetString());
        Assert.Equal("BgcI", full.GetProperty("responseBodyBase64").GetString());
    }
}
