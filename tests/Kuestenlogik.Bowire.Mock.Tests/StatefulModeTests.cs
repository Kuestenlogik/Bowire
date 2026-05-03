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
/// Phase 3b: stateful mode steps through the recording in order — step N+1
/// can only respond after step N has been hit. Exercises the cursor's
/// read-check-advance contract and the wrap-around/once end-of-recording
/// behaviours.
/// </summary>
public sealed class StatefulModeTests
{
    [Fact]
    public async Task Stateful_AdvancesThroughStepsInOrder()
    {
        // Three steps with the same path so the stateless matcher would
        // always hit step 0. Stateful mode must pick step 0, then step 1,
        // then step 2 on successive requests.
        var recording = ThreeStepsSamePath();

        using var host = BuildHost(recording, stateful: true, wrap: true);
        var client = host.GetTestClient();

        var r1 = await client.GetAsync(new Uri("/flow", UriKind.Relative), TestContext.Current.CancellationToken);
        var r2 = await client.GetAsync(new Uri("/flow", UriKind.Relative), TestContext.Current.CancellationToken);
        var r3 = await client.GetAsync(new Uri("/flow", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal("step-one", await r1.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("step-two", await r2.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("step-three", await r3.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stateful_WrapAround_LoopsBackToStepZero()
    {
        var recording = ThreeStepsSamePath();

        using var host = BuildHost(recording, stateful: true, wrap: true);
        var client = host.GetTestClient();

        for (var i = 0; i < 3; i++)
        {
            _ = await client.GetAsync(new Uri("/flow", UriKind.Relative), TestContext.Current.CancellationToken);
        }

        // 4th request wraps back to step one.
        var r4 = await client.GetAsync(new Uri("/flow", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r4.StatusCode);
        Assert.Equal("step-one", await r4.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StatefulOnce_ReturnsMissAfterLastStep()
    {
        var recording = ThreeStepsSamePath();

        using var host = BuildHost(recording, stateful: true, wrap: false);
        var client = host.GetTestClient();

        for (var i = 0; i < 3; i++)
        {
            _ = await client.GetAsync(new Uri("/flow", UriKind.Relative), TestContext.Current.CancellationToken);
        }

        // 4th request: no step left, pass-through lands on the fallthrough
        // teapot we wired into the test host.
        var r4 = await client.GetAsync(new Uri("/flow", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)418, r4.StatusCode);
    }

    [Fact]
    public async Task Stateful_OutOfOrderRequest_MissesNotAdvances()
    {
        // Two steps with *different* paths. A request for step 1's path
        // before step 0 has been hit must miss — stateful mode is strict,
        // the cursor doesn't scan ahead.
        var recording = new BowireRecording
        {
            Id = "rec_order",
            Name = "order",
            RecordingFormatVersion = 2,
            Steps =
            {
                Rest("step_a", "/a", "alpha"),
                Rest("step_b", "/b", "beta")
            }
        };

        using var host = BuildHost(recording, stateful: true, wrap: true);
        var client = host.GetTestClient();

        // Ask for /b first (cursor is at /a) → fallthrough teapot.
        var miss = await client.GetAsync(new Uri("/b", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)418, miss.StatusCode);

        // Now hit /a in order → step advances, next /b works.
        var hitA = await client.GetAsync(new Uri("/a", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal("alpha", await hitA.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var hitB = await client.GetAsync(new Uri("/b", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal("beta", await hitB.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stateful_StatelessModeIgnoresCursor()
    {
        // Sanity check: without --stateful set, the stateless matcher
        // keeps returning the first matching step forever. Same three
        // steps + same path as the ordering test, but in stateless mode
        // every request hits step one.
        var recording = ThreeStepsSamePath();

        using var host = BuildHost(recording, stateful: false, wrap: true);
        var client = host.GetTestClient();

        for (var i = 0; i < 3; i++)
        {
            var r = await client.GetAsync(new Uri("/flow", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal("step-one", await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    // ---- helpers ----

    private static BowireRecording ThreeStepsSamePath() => new()
    {
        Id = "rec_flow",
        Name = "flow",
        RecordingFormatVersion = 2,
        Steps =
        {
            Rest("step_1", "/flow", "step-one"),
            Rest("step_2", "/flow", "step-two"),
            Rest("step_3", "/flow", "step-three")
        }
    };

    private static BowireRecordingStep Rest(string id, string path, string body) => new()
    {
        Id = id,
        Protocol = "rest",
        Service = "Flow",
        Method = id,
        MethodType = "Unary",
        HttpPath = path,
        HttpVerb = "GET",
        Status = "OK",
        Response = body
    };

    private static IHost BuildHost(BowireRecording recording, bool stateful, bool wrap)
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
                            opts.Stateful = stateful;
                            opts.StatefulWrapAround = wrap;
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
