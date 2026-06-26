// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Interceptor;
using Kuestenlogik.Bowire.Recording;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Interceptor;

/// <summary>
/// End-to-end coverage for the in-process interceptor (#153) — boots a
/// Kestrel host with <c>UseBowireInterceptor()</c> + a handful of
/// endpoints, drives real HTTP through it, then asserts the
/// <see cref="InterceptedFlowStore"/> sees the request shape, body, and
/// response intact. The acceptance test on the issue: a host with the
/// interceptor on returns identical responses to a baseline run for
/// non-modified traffic.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class BowireInterceptorMiddlewareTests
{
    private static async Task<(WebApplication app, HttpClient http, InterceptedFlowStore store, BowireRecordingSession session, InterceptorMockStore mocks)> StartAsync(
        CancellationToken ct,
        Action<BowireInterceptorOptions>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        builder.Services.AddBowireInterceptorCore();
        builder.Services.AddSingleton<BowireRecordingSession>();

        var app = builder.Build();
        app.UseBowireInterceptor(configure);
        app.MapGet("/api/hello", () => Results.Ok(new { greeting = "hi" }));
        app.MapPost("/api/echo", async (HttpContext ctx) =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            var text = await sr.ReadToEndAsync();
            return Results.Text(text, "application/json");
        });
        app.MapGet("/api/boom", () => { throw new InvalidOperationException("kaboom"); });
        app.MapGet("/api/stream", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("data: hello\n\n");
            await ctx.Response.Body.FlushAsync();
        });

        await app.StartAsync(ct);
        var addr = app.Urls.First();
        var http = new HttpClient { BaseAddress = new Uri(addr) };
        var store = app.Services.GetRequiredService<InterceptedFlowStore>();
        var session = app.Services.GetRequiredService<BowireRecordingSession>();
        var mocks = app.Services.GetRequiredService<InterceptorMockStore>();
        return (app, http, store, session, mocks);
    }

    [Fact]
    public async Task GetRequest_IsRecordedWithMethodAndStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store, _, _) = await StartAsync(ct);
        await using var _app = app;
        using var _http = http;

        using var resp = await http.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("hi", body, StringComparison.Ordinal);

        var snap = store.Snapshot();
        Assert.Single(snap);
        var flow = snap[0];
        Assert.Equal("GET", flow.Method);
        Assert.Equal(200, flow.ResponseStatus);
        Assert.Contains("/api/hello", flow.Url, StringComparison.Ordinal);
        Assert.Contains("hi", flow.ResponseBody ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostRequest_BodyIsCapturedAndRewoundForEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store, _, _) = await StartAsync(ct);
        await using var _app = app;
        using var _http = http;

        var payload = "{\"name\":\"ada\"}";
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(new Uri("/api/echo", UriKind.Relative), content, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // The endpoint echoes the body back — proves rewinding worked.
        var echoed = await resp.Content.ReadAsStringAsync(ct);
        Assert.Equal(payload, echoed);

        var flow = Assert.Single(store.Snapshot());
        Assert.Equal("POST", flow.Method);
        Assert.Equal(payload, flow.RequestBody);
        Assert.Equal(payload, flow.ResponseBody);
    }

    [Fact]
    public async Task IgnoredPathPrefix_SkipsRecording()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store, _, _) = await StartAsync(ct, opt =>
        {
            opt.IgnoredPathPrefixes.Add("/api/hello");
        });
        await using var _app = app;
        using var _http = http;

        using var resp = await http.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public async Task DisabledOption_BypassesInterceptor()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store, _, _) = await StartAsync(ct, opt => opt.Enabled = false);
        await using var _app = app;
        using var _http = http;

        using var resp = await http.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public async Task StreamingResponse_IsFlaggedAndBodyNotBuffered()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store, _, _) = await StartAsync(ct);
        await using var _app = app;
        using var _http = http;

        using var resp = await http.GetAsync(new Uri("/api/stream", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // Drain so the host's request completes before we inspect the store.
        _ = await resp.Content.ReadAsStringAsync(ct);

        var flow = Assert.Single(store.Snapshot());
        Assert.True(flow.Streaming);
        Assert.Null(flow.ResponseBody);
    }

    [Fact]
    public async Task RecordingSessionActive_AutoAppendsInterceptedFlowAsStep()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store, session, _) = await StartAsync(ct);
        await using var _app = app;
        using var _http = http;

        var started = session.Start("ws-test", BowireRecordingMode.Capture, name: "interceptor demo");
        Assert.Equal(0, started.StepCount);

        using var resp = await http.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Single(store.Snapshot());
        var active = session.Active;
        Assert.NotNull(active);
        Assert.Equal(1, active!.StepCount);
        Assert.Equal("GET", active.SnapshotBuffer[0].Method);
    }

    [Fact]
    public async Task PassThrough_ReturnsIdenticalResponseAsBaseline()
    {
        var ct = TestContext.Current.CancellationToken;

        // Baseline — no interceptor at all.
        var baselineBuilder = WebApplication.CreateSlimBuilder();
        baselineBuilder.Logging.ClearProviders();
        baselineBuilder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        await using var baseline = baselineBuilder.Build();
        baseline.MapGet("/api/hello", () => Results.Ok(new { greeting = "hi" }));
        await baseline.StartAsync(ct);
        using var baselineHttp = new HttpClient { BaseAddress = new Uri(baseline.Urls.First()) };
        using var baselineResp = await baselineHttp.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);
        var baselineBody = await baselineResp.Content.ReadAsStringAsync(ct);
        await baseline.StopAsync(ct);

        var (app, http, _, _, _) = await StartAsync(ct);
        await using var _app = app;
        using var _http = http;
        using var ireResp = await http.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);
        var ireBody = await ireResp.Content.ReadAsStringAsync(ct);

        Assert.Equal(baselineResp.StatusCode, ireResp.StatusCode);
        Assert.Equal(baselineBody, ireBody);
    }

    [Fact]
    public async Task MockRule_ShortCircuitsPipeline_AndFlowsAreLabelledMocked()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store, _, mocks) = await StartAsync(ct);
        await using var _app = app;
        using var _http = http;

        // Seed a mock for /api/hello — the registered endpoint returns
        // {"greeting":"hi"}, the rule overrides it with a different body
        // + a non-200 status, which proves the endpoint never ran.
        mocks.Add(new InterceptorMockRule
        {
            PathPattern = "/api/hello",
            Method = "GET",
            ResponseStatus = 418,
            ResponseBody = "{\"greeting\":\"mocked\"}",
        });

        using var resp = await http.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);
        Assert.Equal((HttpStatusCode)418, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Equal("{\"greeting\":\"mocked\"}", body);

        var flow = Assert.Single(store.Snapshot());
        Assert.True(flow.Mocked);
        Assert.Equal(418, flow.ResponseStatus);
        Assert.Equal("{\"greeting\":\"mocked\"}", flow.ResponseBody);
    }

    [Fact]
    public async Task MockRule_DisabledMocksOption_StillForwardsToEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store, _, mocks) = await StartAsync(ct, opt => opt.MocksEnabled = false);
        await using var _app = app;
        using var _http = http;

        mocks.Add(new InterceptorMockRule
        {
            PathPattern = "/api/hello",
            Method = "GET",
            ResponseStatus = 418,
            ResponseBody = "{\"greeting\":\"mocked\"}",
        });

        using var resp = await http.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("hi", body, StringComparison.Ordinal);
        Assert.False(Assert.Single(store.Snapshot()).Mocked);
    }

    [Fact]
    public async Task MockRule_WithWildcard_MatchesSubpaths()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store, _, mocks) = await StartAsync(ct);
        await using var _app = app;
        using var _http = http;

        mocks.Add(new InterceptorMockRule
        {
            PathPattern = "/api/*",
            Method = "*",
            ResponseStatus = 200,
            ResponseBody = "{\"source\":\"wildcard\"}",
        });

        using var resp = await http.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("wildcard", body, StringComparison.Ordinal);
        Assert.True(Assert.Single(store.Snapshot()).Mocked);
    }

    [Fact]
    public async Task UseBowireInterceptor_WithoutCoreRegistration_ThrowsHelpfulError()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        await using var app = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => app.UseBowireInterceptor());
        Assert.Contains("InterceptedFlowStore", ex.Message, StringComparison.Ordinal);
        await Task.CompletedTask;
        _ = ct;
    }
}
