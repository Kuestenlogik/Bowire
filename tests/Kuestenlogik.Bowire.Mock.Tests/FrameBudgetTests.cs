// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Mock.Chaos;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// "Drop after N frames" (#170) — a fault that cuts a streaming replay
/// short in the unit a streaming client reasons in.
/// </summary>
/// <remarks>
/// <c>partialBytes</c> was the only cap, and it is the right unit for a
/// unary body and the wrong one for a stream: it cuts an event in half,
/// so the client sees a parse error rather than a stream that stopped.
/// These tests are about the difference — how many whole frames arrive,
/// and that the ones that do arrive are intact.
/// </remarks>
public sealed class FrameBudgetTests
{
    [Fact]
    public void LoadJson_PartialFrames_Parses_And_RoundTrips()
    {
        var set = FaultRuleSet.LoadJson("""
        { "rules": [ { "kind": "partial-response", "partialFrames": 3 } ] }
        """);

        var rule = Assert.Single(set.Rules);
        Assert.Equal(3, rule.PartialFrames);
        Assert.Contains("partialFrames", set.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_Rule_Without_PartialFrames_Caps_Nothing()
    {
        var set = FaultRuleSet.LoadJson("""
        { "rules": [ { "kind": "partial-response", "partialBytes": 512 } ] }
        """);

        // 0 means "no frame cap" — a byte-capped rule must not start
        // truncating streams that were never in scope for it.
        Assert.Equal(0, Assert.Single(set.Rules).PartialFrames);
    }

    [Fact]
    public async Task Sse_StopsAfterTheBudget_AndTheFramesThatArriveAreWhole()
    {
        using var host = BuildHost(FiveFrames(), """
        { "rules": [ { "kind": "partial-response", "partialFrames": 2, "partialBytes": 1000000 } ] }
        """);
        var client = host.GetTestClient();

        var body = await client.GetStringAsync(new Uri("/events", UriKind.Relative), TestContext.Current.CancellationToken);

        // Two events, and both complete — the point of counting frames
        // rather than bytes.
        Assert.Contains("""{"n":0}""", body, StringComparison.Ordinal);
        Assert.Contains("""{"n":1}""", body, StringComparison.Ordinal);
        Assert.DoesNotContain("""{"n":2}""", body, StringComparison.Ordinal);
        Assert.DoesNotContain("""{"n":4}""", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Budget_Larger_Than_The_Stream_Changes_Nothing()
    {
        using var host = BuildHost(FiveFrames(), """
        { "rules": [ { "kind": "partial-response", "partialFrames": 99 } ] }
        """);
        var client = host.GetTestClient();

        var body = await client.GetStringAsync(new Uri("/events", UriKind.Relative), TestContext.Current.CancellationToken);

        for (var n = 0; n < 5; n++)
            Assert.Contains($$"""{"n":{{n}}}""", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_A_Rule_Every_Frame_Arrives()
    {
        using var host = BuildHost(FiveFrames(), faultsJson: null);
        var client = host.GetTestClient();

        var body = await client.GetStringAsync(new Uri("/events", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("""{"n":4}""", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Byte_Cap_Alone_Still_Cuts_Mid_Frame()
    {
        // The behaviour #170 exists to complement, pinned so the two caps
        // stay distinguishable: bytes land wherever they land.
        using var host = BuildHost(FiveFrames(), """
        { "rules": [ { "kind": "partial-response", "partialBytes": 12 } ] }
        """);
        var client = host.GetTestClient();

        var body = await client.GetStringAsync(new Uri("/events", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(12, body.Length);
        Assert.DoesNotContain("""{"n":1}""", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Rule_For_Another_Method_Leaves_The_Stream_Alone()
    {
        using var host = BuildHost(FiveFrames(), """
        { "rules": [ { "method": "OrderService/*", "kind": "partial-response", "partialFrames": 1 } ] }
        """);
        var client = host.GetTestClient();

        var body = await client.GetStringAsync(new Uri("/events", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("""{"n":4}""", body, StringComparison.Ordinal);
    }

    private static BowireRecording FiveFrames() => new()
    {
        Id = "rec_frame_budget",
        Name = "frame-budget",
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

    // A null faultsJson configures no faults at all. An empty rule list is
    // not the same thing, and LoadJson refuses it.
    private static IHost BuildHost(BowireRecording recording, string? faultsJson)
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
                            if (faultsJson is not null)
                                opts.Faults = FaultRuleSet.LoadJson(faultsJson);
                        });
                    });
            })
            .Start();
    }
}
