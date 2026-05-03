// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// End-to-end REST-replay tests against Microsoft.AspNetCore.TestHost.
/// Exercises the same production code path as embedded use — only the
/// host infrastructure differs.
/// </summary>
public sealed class IntegrationTests
{
    private static BowireRecording SampleRecording() => new()
    {
        Id = "rec_test",
        Name = "sample",
        RecordingFormatVersion = 1,
        Steps =
        {
            new BowireRecordingStep
            {
                Id = "step_weather",
                Protocol = "rest",
                Service = "WeatherService",
                Method = "GetForecast",
                MethodType = "Unary",
                HttpPath = "/weather",
                HttpVerb = "GET",
                Status = "OK",
                Response = """{"temp":21,"city":"Berlin"}"""
            },
            new BowireRecordingStep
            {
                Id = "step_not_found",
                Protocol = "rest",
                Service = "WeatherService",
                Method = "GetUnknown",
                MethodType = "Unary",
                HttpPath = "/weather/unknown",
                HttpVerb = "GET",
                Status = "NotFound",
                Response = """{"error":"no such station"}"""
            },
            new BowireRecordingStep
            {
                Id = "step_streaming",
                Protocol = "rest",
                Service = "WeatherService",
                Method = "Stream",
                MethodType = "ServerStreaming",
                HttpPath = "/weather/stream",
                HttpVerb = "GET",
                Status = "OK",
                Response = null
            }
        }
    };

    private static IHost BuildHost(Action<MockOptions>? configure = null, BowireRecording? recording = null)
    {
        var rec = recording ?? SampleRecording();
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseBowireMock(rec, opts =>
                       {
                           opts.Watch = false;
                           configure?.Invoke(opts);
                       });
                       app.Run(async ctx =>
                       {
                           ctx.Response.StatusCode = 418; // I'm a teapot — signals the fallthrough reached the tail.
                           await ctx.Response.WriteAsync("fallthrough");
                       });
                   });
            })
            .Start();
    }

    [Fact]
    public async Task GetMatchingPath_ReplaysRecordedResponse()
    {
        using var host = BuildHost();
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/weather", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("""{"temp":21,"city":"Berlin"}""", body);
    }

    [Fact]
    public async Task GetRecordedNotFound_ReplaysStatusCodeAndBody()
    {
        using var host = BuildHost();
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/weather/unknown", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("""{"error":"no such station"}""", body);
    }

    [Fact]
    public async Task GetUnmatchedPath_PassesThroughByDefault()
    {
        using var host = BuildHost();
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/not/in/recording", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)418, resp.StatusCode);
        Assert.Equal("fallthrough", await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetUnmatchedPath_Returns404_WhenPassThroughDisabled()
    {
        using var host = BuildHost(opts => opts.PassThroughOnMiss = false);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/not/in/recording", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task StreamingStepWithoutReceivedMessages_Returns501()
    {
        // The SampleRecording's streaming step has no receivedMessages — it
        // was captured by a Phase-1a build before per-frame capture shipped.
        // The replayer should return a 501 with a clear "re-record" message
        // rather than a silently-wrong empty stream.
        using var host = BuildHost(opts => opts.PassThroughOnMiss = false);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/weather/stream", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("receivedMessages", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SseStreaming_ReplaysRecordedFramesInOrder()
    {
        // Phase 2c: a recorded ServerStreaming REST step with three frames
        // in receivedMessages. The mock should emit them as SSE 'data: …'
        // events in order, producing the original payloads verbatim.
        var rec = new BowireRecording
        {
            Id = "rec_sse",
            Name = "sse",
            RecordingFormatVersion = 2,
            Steps =
            {
                new BowireRecordingStep
                {
                    Id = "step_events",
                    Protocol = "rest",
                    Service = "Events",
                    Method = "Stream",
                    MethodType = "ServerStreaming",
                    HttpPath = "/events",
                    HttpVerb = "GET",
                    Status = "OK",
                    ReceivedMessages = new List<BowireRecordingFrame>
                    {
                        new() { Index = 0, TimestampMs = 0, Data = """{"type":"start"}""" },
                        new() { Index = 1, TimestampMs = 5, Data = """{"type":"tick","n":1}""" },
                        new() { Index = 2, TimestampMs = 10, Data = """{"type":"end"}""" }
                    }
                }
            }
        };

        using var host = BuildHost(opts => opts.ReplaySpeed = 0, recording: rec);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/events", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // Three events, each with `id: <index>` + `data: <json>`, in order.
        Assert.Contains("id: 0\ndata: {\"type\":\"start\"}\n\n", body, StringComparison.Ordinal);
        Assert.Contains("id: 1\ndata: {\"type\":\"tick\",\"n\":1}\n\n", body, StringComparison.Ordinal);
        Assert.Contains("id: 2\ndata: {\"type\":\"end\"}\n\n", body, StringComparison.Ordinal);
        // Order matters for a stream — 'start' must come before 'end'.
        Assert.True(
            body.IndexOf("start", StringComparison.Ordinal) <
            body.IndexOf("end", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SseStreaming_RespectsPerFrameTimestampPacing()
    {
        // With ReplaySpeed = 1.0, the mock should honour the per-frame
        // timestampMs deltas. Two frames 120 ms apart should take at least
        // ~100 ms end-to-end — tolerate OS scheduling jitter on the upper
        // bound.
        var rec = new BowireRecording
        {
            Id = "rec_sse_pace",
            Name = "sse pace",
            RecordingFormatVersion = 2,
            Steps =
            {
                new BowireRecordingStep
                {
                    Id = "step_paced",
                    Protocol = "rest",
                    Service = "Events",
                    Method = "Stream",
                    MethodType = "ServerStreaming",
                    HttpPath = "/paced",
                    HttpVerb = "GET",
                    Status = "OK",
                    ReceivedMessages = new List<BowireRecordingFrame>
                    {
                        new() { Index = 0, TimestampMs = 0, Data = "\"first\"" },
                        new() { Index = 1, TimestampMs = 120, Data = "\"second\"" }
                    }
                }
            }
        };

        using var host = BuildHost(opts => opts.ReplaySpeed = 1.0, recording: rec);
        var client = host.GetTestClient();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await client.GetAsync(new Uri("/paced", UriKind.Relative), TestContext.Current.CancellationToken);
        _ = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 100,
            $"Expected at least 100 ms of paced output, got {sw.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task DynamicPlaceholders_InResponseBody_AreResolvedPerRequest()
    {
        // Phase 2b end-to-end: a recording body with ${uuid} and ${now}
        // placeholders. Two successive calls should return different values
        // for the dynamic fields, proving substitution fires per-request.
        var rec = new BowireRecording
        {
            Id = "rec_dyn",
            Name = "dynamic",
            RecordingFormatVersion = 2,
            Steps =
            {
                new BowireRecordingStep
                {
                    Id = "step_dyn",
                    Protocol = "rest",
                    Service = "X",
                    Method = "Y",
                    MethodType = "Unary",
                    HttpPath = "/echo",
                    HttpVerb = "GET",
                    Status = "OK",
                    Response = """{"id":"${uuid}","created":${now}}"""
                }
            }
        };

        using var host = BuildHost(recording: rec);
        var client = host.GetTestClient();

        var a = await client.GetAsync(new Uri("/echo", UriKind.Relative), TestContext.Current.CancellationToken);
        var b = await client.GetAsync(new Uri("/echo", UriKind.Relative), TestContext.Current.CancellationToken);
        var bodyA = await a.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var bodyB = await b.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("${", bodyA, StringComparison.Ordinal);
        Assert.DoesNotContain("${", bodyB, StringComparison.Ordinal);
        Assert.NotEqual(bodyA, bodyB);  // different uuids, possibly different now
    }
}
