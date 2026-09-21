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
    /// <summary>
    /// Every request each test host saw, as the connection saw it (#714).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The intermittent failure was two flows for one request, and the
    /// conclusion drawn was "a second real request reached this host" —
    /// which is sound — followed by "from a foreign client", which is not:
    /// nothing in the evidence distinguishes a stranger from this test's own
    /// client sending twice. The flows carry method and path; they do not
    /// carry who sent them, and that is the one thing that would settle it.
    /// </para>
    /// <para>
    /// So the host writes down the remote endpoint, the connection id and
    /// the user agent of everything that arrives, and the assertions print
    /// it. Two flows on one connection id are ours. Two connections from
    /// two remote ports, one with a user agent we never set, are not.
    /// </para>
    /// <para>
    /// Keyed by the store because the helpers already receive it and the
    /// alternative is threading a sixth element through every call site of
    /// <c>StartAsync</c>. A weak table so a finished test's host is still
    /// collectable.
    /// </para>
    /// </remarks>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        InterceptedFlowStore, System.Collections.Concurrent.ConcurrentQueue<string>> s_arrivals = new();

    private static string ArrivalsFor(InterceptedFlowStore store)
    {
        if (!s_arrivals.TryGetValue(store, out var log) || log.IsEmpty)
        {
            return "nothing recorded";
        }
        return string.Join(" | ", log);
    }

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

        // Ahead of the interceptor so it sees requests the interceptor
        // skips too — an ignored path, a disabled run, and whatever else
        // arrives on this port (#714).
        var arrivals = new System.Collections.Concurrent.ConcurrentQueue<string>();
        s_arrivals.Add(app.Services.GetRequiredService<InterceptedFlowStore>(), arrivals);
        app.Use(async (HttpContext ctx, RequestDelegate next) =>
        {
            arrivals.Enqueue(
                $"{ctx.Request.Method} {ctx.Request.Path} "
                + $"from {ctx.Connection.RemoteIpAddress}:{ctx.Connection.RemotePort} "
                + $"conn={ctx.Connection.Id} "
                + $"ua={(string?)ctx.Request.Headers.UserAgent ?? "(none)"} "
                // The one mechanism in this repository that would send a
                // loopback request somewhere it did not mean to: the
                // reverse-proxy host forwards to a recorded upstream
                // 127.0.0.1:port, and YARP stamps these on the way through.
                // Set means the request was forwarded, not sent here.
                + $"fwd={(string?)ctx.Request.Headers["X-Forwarded-For"] ?? "(none)"}");
            await next(ctx);
        });

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
        // Large fixed-size text body for response-truncation coverage.
        app.MapGet("/api/large", () => Results.Text(new string('x', 5000), "text/plain"));

        await app.StartAsync(ct);
        var addr = app.Urls.First();
        var http = new HttpClient { BaseAddress = new Uri(addr) };
        var store = app.Services.GetRequiredService<InterceptedFlowStore>();
        var session = app.Services.GetRequiredService<BowireRecordingSession>();
        var mocks = app.Services.GetRequiredService<InterceptorMockStore>();
        return (app, http, store, session, mocks);
    }

    // The response is socket-flushed to the client BEFORE the middleware
    // reaches RecordFlow — on every path: mocks write straight to the real
    // stream, forwarded responses during CopyToAsync. The client can
    // therefore observe completion while the server-side bookkeeping is
    // still pending, so every "response received → flow recorded" assertion
    // must poll instead of snapshotting immediately.
    private static async Task WaitUntilAsync(
        Func<bool> condition, CancellationToken ct, string what = "the expected state")
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10, ct);
        }

        // #714 - this used to return quietly when the deadline passed. The
        // caller then asserted against whatever it had, and a timeout
        // arrived looking like a content mismatch somewhere further down.
        Assert.True(condition(), $"Timed out after 5s waiting for {what}.");
    }

    /// <summary>
    /// The flow this test's own request produced (#714).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be <c>Assert.Single(store.Snapshot())</c>, which asserts
    /// that the store saw nothing but our request — a property the test does
    /// not establish and the product deliberately does not have: the
    /// interceptor records what arrives, not what a test sent.
    /// </para>
    /// <para>
    /// It failed intermittently under a solution-wide run with two flows,
    /// ids 1 and 2, 2.2ms apart. A second real request reached the host; the
    /// leading theory is an ephemeral port recycled between hosts while
    /// another client still aimed at it, but that was never established
    /// because the failure message did not name the paths. Hence the
    /// message below: the next occurrence answers the question instead of
    /// hiding it again.
    /// </para>
    /// </remarks>
    private static async Task<InterceptedFlow> WaitForFlowAsync(
        InterceptedFlowStore store, string path, CancellationToken ct)
    {
        await WaitUntilAsync(
            () => store.Snapshot().Any(f => f.Path == path), ct,
            $"a flow for {path}");

        var all = store.Snapshot();
        var mine = all.Where(f => f.Path == path).ToList();
        Assert.True(mine.Count == 1,
            $"Expected exactly one flow for {path}, saw {mine.Count}. "
            + "All flows in this store: "
            + string.Join(", ", all.Select(f => $"#{f.Id} {f.Method} {f.Path}"))
            + ". Everything that arrived on this port: " + ArrivalsFor(store));
        return mine[0];
    }

    /// <summary>
    /// No flow for this path — with the same message the positive helper
    /// gives, because "it is not there" is the assertion most in need of
    /// saying what was there instead (#714).
    /// </summary>
    private static void AssertNoFlowFor(InterceptedFlowStore store, string path)
    {
        var all = store.Snapshot();
        Assert.True(all.All(f => f.Path != path),
            $"Expected no flow for {path}, but one was recorded. "
            + "All flows in this store: "
            + string.Join(", ", all.Select(f => $"#{f.Id} {f.Method} {f.Path}"))
            + ". Everything that arrived on this port: " + ArrivalsFor(store));
    }

    /// <summary>
    /// Drive one request that IS recorded and wait for its flow, so a
    /// following "nothing was recorded" assertion has something to stand on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The flow is written after the response has been flushed to the client,
    /// so nothing guarantees that a snapshot taken the moment the request
    /// under test answers already carries it. Requests on one connection are
    /// served in order, so once this barrier's flow is in, an earlier
    /// request's would be too — which turns "not recorded" from a question
    /// of timing into one the store can answer.
    /// </para>
    /// <para>
    /// Measured, not assumed, and it cuts the other way than expected: with
    /// the ignore rule removed the old immediate assertion failed three
    /// times out of three, so the flow does land before the client's
    /// response completes. It simply is not promised anywhere, and this is
    /// cheaper than depending on it.
    /// </para>
    /// </remarks>
    private static async Task RecordedBarrierAsync(
        HttpClient http, InterceptedFlowStore store, CancellationToken ct)
    {
        using var barrier = await http.GetAsync(new Uri("/api/large", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, barrier.StatusCode);
        _ = await barrier.Content.ReadAsStringAsync(ct);
        await WaitForFlowAsync(store, "/api/large", ct);
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

        var flow = await WaitForFlowAsync(store, "/api/hello", ct);
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

        var flow = await WaitForFlowAsync(store, "/api/echo", ct);
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

        // #714 — the assertion was that the store is empty, which is a claim
        // about every request that reached this host rather than about the
        // ignored one. That is the property the intermittent failure broke,
        // and it is not one this test establishes. The barrier is the smaller
        // point: it makes the moment of asking an ordering rather than a
        // coincidence (see RecordedBarrierAsync).
        await RecordedBarrierAsync(http, store, ct);
        AssertNoFlowFor(store, "/api/hello");
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

        // No barrier here, and Empty rather than "no flow for this path", on
        // purpose: with the interceptor off nothing reaches the store at all,
        // so there is no request — ours or anybody else's — whose flow could
        // appear later or interfere. The other skip (#714) needed both; this
        // one is the stronger statement and the one worth making.
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

        var flow = await WaitForFlowAsync(store, "/api/stream", ct);
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

        await WaitForFlowAsync(store, "/api/hello", ct);
        // RecordFlow adds to the store BEFORE appending the recording step —
        // wait for the step separately instead of piggybacking on the store.
        await WaitUntilAsync(() => session.Active?.StepCount == 1, ct);
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

        var flow = await WaitForFlowAsync(store, "/api/hello", ct);
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
        Assert.False((await WaitForFlowAsync(store, "/api/hello", ct)).Mocked);
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
        Assert.True((await WaitForFlowAsync(store, "/api/hello", ct)).Mocked);
    }

    [Fact]
    public async Task RequestBody_ExceedingCap_SetsTruncatedFlag_AndEndpointStillSeesFullBody()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store, _, _) = await StartAsync(ct, opt => opt.MaxBodyBytes = 16);
        await using var _app = app;
        using var _http = http;

        var payload = new string('a', 200);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(new Uri("/api/echo", UriKind.Relative), content, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Endpoint echoes the full payload back — rewind survived truncation.
        Assert.Equal(payload, await resp.Content.ReadAsStringAsync(ct));

        // The flow is recorded after the response is flushed to the client,
        // so the client can observe completion before RecordFlow runs — poll
        // the store rather than snapshotting immediately (same race the
        // response-truncation test guards against).
        var flow = await WaitForFlowAsync(store, "/api/echo", ct);
        Assert.True(flow.RequestBodyTruncated);
        Assert.NotNull(flow.RequestBody);
        Assert.Equal(16, flow.RequestBody!.Length);
    }

    [Fact]
    public async Task ResponseBody_ExceedingCap_SetsTruncatedFlag_ClientStillGetsFullBody()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store, _, _) = await StartAsync(ct, opt => opt.MaxBodyBytes = 16);
        await using var _app = app;
        using var _http = http;

        using var resp = await http.GetAsync(new Uri("/api/large", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The cap governs what we KEEP for the rail, not what we forward.
        var clientBody = await resp.Content.ReadAsStringAsync(ct);
        Assert.Equal(5000, clientBody.Length);

        var flow = await WaitForFlowAsync(store, "/api/large", ct);
        Assert.True(flow.ResponseBodyTruncated);
        Assert.Equal(16, flow.ResponseBody!.Length);
    }

    [Fact]
    public async Task BinaryRequestBody_IsCapturedAsBase64_NotText()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store, _, _) = await StartAsync(ct);
        await using var _app = app;
        using var _http = http;

        var binary = new byte[] { 0x01, 0x00, 0x02, 0xFF, 0x10 };
        using var content = new ByteArrayContent(binary);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var resp = await http.PostAsync(new Uri("/api/echo", UriKind.Relative), content, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        _ = await resp.Content.ReadAsStringAsync(ct);

        var flow = await WaitForFlowAsync(store, "/api/echo", ct);
        Assert.Null(flow.RequestBody);
        Assert.NotNull(flow.RequestBodyBase64);
        Assert.Equal(binary, Convert.FromBase64String(flow.RequestBodyBase64!));
    }

    [Fact]
    public async Task EndpointThrows_FlowRecordedWithError_AndExceptionSurfaces()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store, _, _) = await StartAsync(ct);
        await using var _app = app;
        using var _http = http;

        using var resp = await http.GetAsync(new Uri("/api/boom", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        var flow = await WaitForFlowAsync(store, "/api/boom", ct);
        Assert.Equal("GET", flow.Method);
        Assert.NotNull(flow.Error);
        Assert.Contains("kaboom", flow.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MockRule_Base64Body_IsServedAsBinary()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, store, _, mocks) = await StartAsync(ct);
        await using var _app = app;
        using var _http = http;

        var binary = new byte[] { 0x00, 0x01, 0x02, 0xFF };
        mocks.Add(new InterceptorMockRule
        {
            PathPattern = "/api/hello",
            Method = "GET",
            ResponseBodyBase64 = Convert.ToBase64String(binary),
        });

        using var resp = await http.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(binary, await resp.Content.ReadAsByteArrayAsync(ct));
        Assert.True((await WaitForFlowAsync(store, "/api/hello", ct)).Mocked);
    }

    [Fact]
    public async Task MockRule_WithoutContentType_DefaultsToJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, http, _, _, mocks) = await StartAsync(ct);
        await using var _app = app;
        using var _http = http;

        mocks.Add(new InterceptorMockRule
        {
            PathPattern = "/api/hello",
            Method = "GET",
            ResponseBody = "{\"ok\":true}",
        });

        using var resp = await http.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
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
