// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using Kuestenlogik.Bowire.Mock.Chaos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Phase 3a: chaos injection adds latency jitter and/or fails a
/// configurable fraction of matched requests before dispatching to the
/// replayer. Parser + runtime behaviour are covered here; the hosted
/// mock server forwards the same options so CLI and embedded modes share
/// the implementation.
/// </summary>
public sealed class ChaosTests
{
    // ---- parser ----

    [Fact]
    public void Parse_SingleFixedLatency_Populates_Min_Equals_Max()
    {
        var opts = ChaosOptions.Parse("latency:250");
        Assert.Equal(250, opts.LatencyMinMs);
        Assert.Equal(250, opts.LatencyMaxMs);
        Assert.Equal(0, opts.FailRate);
    }

    [Fact]
    public void Parse_LatencyRange_PopulatesBounds()
    {
        var opts = ChaosOptions.Parse("latency:100-500");
        Assert.Equal(100, opts.LatencyMinMs);
        Assert.Equal(500, opts.LatencyMaxMs);
    }

    [Fact]
    public void Parse_FailRate_Populates()
    {
        var opts = ChaosOptions.Parse("fail-rate:0.05");
        Assert.Equal(0.05, opts.FailRate);
        Assert.Equal(503, opts.FailStatusCode);
    }

    [Fact]
    public void Parse_Combined_PopulatesEveryKnob()
    {
        var opts = ChaosOptions.Parse("latency:10-20,fail-rate:0.5,fail-status:500");
        Assert.Equal(10, opts.LatencyMinMs);
        Assert.Equal(20, opts.LatencyMaxMs);
        Assert.Equal(0.5, opts.FailRate);
        Assert.Equal(500, opts.FailStatusCode);
        Assert.True(opts.IsActive);
    }

    [Fact]
    public void Parse_UnknownKey_Throws()
    {
        Assert.Throws<FormatException>(() => ChaosOptions.Parse("jitter:100"));
    }

    [Fact]
    public void Parse_InvertedLatencyRange_Throws()
    {
        Assert.Throws<FormatException>(() => ChaosOptions.Parse("latency:500-100"));
    }

    [Fact]
    public void Parse_FailRateOutOfRange_Throws()
    {
        Assert.Throws<FormatException>(() => ChaosOptions.Parse("fail-rate:1.5"));
    }

    // ---- runtime ----

    [Fact]
    public async Task FailRate_One_AlwaysReturnsFailStatus()
    {
        // fail-rate:1.0 turns every matched request into the fail-status
        // response (503 by default). Deterministic — no flakes.
        var rec = SimpleRestRecording();

        using var host = BuildHost(rec, new ChaosOptions { FailRate = 1.0 });
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/ping", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("chaos", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailRate_Zero_PassesThroughToReplayer()
    {
        // fail-rate:0 + no latency = chaos doesn't touch the request; the
        // replayer serves the recorded body normally.
        var rec = SimpleRestRecording();

        using var host = BuildHost(rec, new ChaosOptions { FailRate = 0 });
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/ping", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("pong", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailStatus_Override_IsRespected()
    {
        var rec = SimpleRestRecording();

        using var host = BuildHost(rec, new ChaosOptions { FailRate = 1.0, FailStatusCode = 500 });
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/ping", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
    }

    [Fact]
    public async Task FixedLatency_DelaysResponseByAtLeastThatMuch()
    {
        // A fixed latency of 150ms should show up in the request's wall
        // clock. 100ms lower bound allows for CI timer jitter without
        // being lenient enough to hide a broken delay.
        var rec = SimpleRestRecording();

        using var host = BuildHost(rec, new ChaosOptions { LatencyMinMs = 150, LatencyMaxMs = 150 });
        var client = host.GetTestClient();

        var sw = Stopwatch.StartNew();
        var resp = await client.GetAsync(new Uri("/ping", UriKind.Relative), TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(sw.ElapsedMilliseconds >= 100,
            $"Expected chaos latency to delay the response by ~150ms, but request took only {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task UnmatchedRequest_SkipsChaos()
    {
        // Chaos only fires after a match. An unmatched path should still
        // get the standalone-host 404 immediately, without latency or a
        // chaos-shaped body — otherwise a mock with fail-rate:1 would
        // turn every stray request into a 503 and mask misses.
        var rec = SimpleRestRecording();

        using var host = BuildHost(rec, new ChaosOptions { FailRate = 1.0, LatencyMinMs = 5000, LatencyMaxMs = 5000 });
        var client = host.GetTestClient();

        var sw = Stopwatch.StartNew();
        var resp = await client.GetAsync(new Uri("/unknown", UriKind.Relative), TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.Equal((HttpStatusCode)418, resp.StatusCode); // fallthrough teapot
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"Expected unmatched request to skip chaos latency, but it took {sw.ElapsedMilliseconds}ms.");
    }

    // ---- helpers ----

    private static BowireRecording SimpleRestRecording() => new()
    {
        Id = "rec_chaos",
        Name = "chaos",
        RecordingFormatVersion = 2,
        Steps =
        {
            new BowireRecordingStep
            {
                Id = "step_ping",
                Protocol = "rest",
                Service = "Ping",
                Method = "Ping",
                MethodType = "Unary",
                HttpPath = "/ping",
                HttpVerb = "GET",
                Status = "OK",
                Response = "{\"message\":\"pong\"}"
            }
        }
    };

    private static IHost BuildHost(BowireRecording recording, ChaosOptions chaos)
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
                            opts.PassThroughOnMiss = true;
                            opts.ReplaySpeed = 0;
                            opts.Chaos = chaos;
                        });
                        app.Run(async ctx =>
                        {
                            ctx.Response.StatusCode = 418;
                            await ctx.Response.WriteAsync("fallthrough");
                        });
                    });
            })
            .Start();
    }
}
