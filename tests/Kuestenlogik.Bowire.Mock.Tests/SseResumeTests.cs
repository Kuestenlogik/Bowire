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
/// Phase 2 polish: SSE clients that reconnect after a drop send
/// <c>Last-Event-ID: N</c> so the server knows where they were. The
/// mock now honours the header by skipping frames with Index &lt;= N
/// and resuming from the next one, and always emits the
/// <c>id: &lt;index&gt;</c> line so the client has something to send
/// on the next reconnect.
/// </summary>
public sealed class SseResumeTests
{
    [Fact]
    public async Task FreshRequest_EmitsAllFrames_WithIdLine()
    {
        var rec = BuildFiveFrameRecording();
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/events", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Every frame should carry its Index as the SSE id.
        Assert.Contains("id: 0\ndata: {\"n\":0}\n\n", body, StringComparison.Ordinal);
        Assert.Contains("id: 1\ndata: {\"n\":1}\n\n", body, StringComparison.Ordinal);
        Assert.Contains("id: 2\ndata: {\"n\":2}\n\n", body, StringComparison.Ordinal);
        Assert.Contains("id: 3\ndata: {\"n\":3}\n\n", body, StringComparison.Ordinal);
        Assert.Contains("id: 4\ndata: {\"n\":4}\n\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_LastEventId_2_SkipsFirstThreeFrames()
    {
        // Client "saw" frames 0, 1, 2 before the drop. On reconnect it
        // reports Last-Event-ID: 2. Mock resumes at frame 3.
        var rec = BuildFiveFrameRecording();
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/events");
        req.Headers.Add("Last-Event-ID", "2");
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Frames 0-2 must NOT appear.
        Assert.DoesNotContain("id: 0\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain("id: 1\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain("id: 2\n", body, StringComparison.Ordinal);
        // Frames 3 and 4 do.
        Assert.Contains("id: 3\ndata: {\"n\":3}\n\n", body, StringComparison.Ordinal);
        Assert.Contains("id: 4\ndata: {\"n\":4}\n\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_LastEventIdBeyondEnd_ReturnsEmptyStream()
    {
        // Client claims it's already past every recorded frame.
        var rec = BuildFiveFrameRecording();
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/events");
        req.Headers.Add("Last-Event-ID", "99");
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("data:", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_NonNumericHeader_Ignored_FullStreamReplays()
    {
        // Spec lets the header carry any string; our recorder only
        // ever writes integers. A non-numeric value means "we can't
        // interpret it", so the mock replays from the start rather
        // than swallowing the whole stream silently.
        var rec = BuildFiveFrameRecording();
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/events");
        req.Headers.Add("Last-Event-ID", "not-a-number");
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("id: 0\n", body, StringComparison.Ordinal);
        Assert.Contains("id: 4\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_EmptyHeader_FullStreamReplays()
    {
        var rec = BuildFiveFrameRecording();
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/events");
        req.Headers.Add("Last-Event-ID", "");
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("id: 0\n", body, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static BowireRecording BuildFiveFrameRecording() => new()
    {
        Id = "rec_sse_resume",
        Name = "sse-resume",
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
                    new() { Index = 0, TimestampMs = 0, Data = """{"n":0}""" },
                    new() { Index = 1, TimestampMs = 1, Data = """{"n":1}""" },
                    new() { Index = 2, TimestampMs = 2, Data = """{"n":2}""" },
                    new() { Index = 3, TimestampMs = 3, Data = """{"n":3}""" },
                    new() { Index = 4, TimestampMs = 4, Data = """{"n":4}""" }
                }
            }
        }
    };

    private static IHost BuildHost(BowireRecording recording)
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                    .Configure(app =>
                    {
                        app.UseBowireMock(recording, opts =>
                        {
                            opts.Watch = false;
                            opts.ReplaySpeed = 0; // instant for tests
                        });
                    });
            })
            .Start();
    }
}
