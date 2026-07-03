// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Mock.Chaos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// #170 — per-method fault rules: mock-faults.json parsing + validation,
/// method-glob matching, deterministic distribution sampling (canned
/// uniform sequences), the truncating response wrapper, and end-to-end
/// behaviour through a hosted mock (error / partial-response /
/// method-scoping).
/// </summary>
public sealed class FaultRulesTests
{
    // ---- parsing + validation ----

    [Fact]
    public void LoadJson_FullDocument_Parses()
    {
        var set = FaultRuleSet.LoadJson("""
        {
          "rules": [
            { "method": "UserService/*", "kind": "error", "rate": 0.25, "errorStatusCode": 500 },
            { "method": "*/Download", "kind": "partial-response", "partialBytes": 512 },
            { "kind": "connection-drop", "partialBytes": 0 },
            { "kind": "latency-only", "latency": { "distribution": "normal", "meanMs": 200, "stdDevMs": 50 } }
          ]
        }
        """);

        Assert.Equal(4, set.Rules.Count);
        Assert.True(set.IsActive);
        Assert.Equal(FaultKind.Error, set.Rules[0].Kind);
        Assert.Equal(0.25, set.Rules[0].Rate);
        Assert.Equal(500, set.Rules[0].ErrorStatusCode);
        Assert.Equal(FaultKind.PartialResponse, set.Rules[1].Kind);
        Assert.Equal(512, set.Rules[1].PartialBytes);
        Assert.Equal(FaultKind.ConnectionDrop, set.Rules[2].Kind);
        Assert.Equal("normal", set.Rules[3].Latency!.Distribution);
    }

    [Theory]
    [InlineData("{ }", "no rules")]
    [InlineData("""{ "rules": [ { "rate": 1.5 } ] }""", "rate")]
    [InlineData("""{ "rules": [ { "errorStatusCode": 42 } ] }""", "errorStatusCode")]
    [InlineData("""{ "rules": [ { "partialBytes": -1 } ] }""", "partialBytes")]
    [InlineData("""{ "rules": [ { "latency": { "distribution": "pareto" } } ] }""", "distribution")]
    [InlineData("""{ "rules": [ { "latency": { "distribution": "uniform", "minMs": 500, "maxMs": 100 } } ] }""", "uniform")]
    [InlineData("not json", "invalid JSON")]
    public void LoadJson_InvalidDocuments_ThrowFormatExceptionWithContext(string json, string expectedFragment)
    {
        var ex = Assert.Throws<FormatException>(() => FaultRuleSet.LoadJson(json));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyRuleSet_IsInactive()
    {
        Assert.False(new FaultRuleSet().IsActive);
        Assert.Null(new FaultRuleSet().FirstMatch("Svc", "M"));
    }

    // ---- method matching ----

    [Theory]
    [InlineData(null, "UserService", "GetUser", true)]
    [InlineData("*", "UserService", "GetUser", true)]
    [InlineData("UserService/GetUser", "UserService", "GetUser", true)]
    [InlineData("userservice/getuser", "UserService", "GetUser", true)] // case-insensitive
    [InlineData("UserService/*", "UserService", "DeleteUser", true)]
    [InlineData("UserService/*", "OrderService", "GetOrder", false)]
    [InlineData("*/Get*", "OrderService", "GetOrder", true)]
    [InlineData("*/Get*", "OrderService", "PlaceOrder", false)]
    public void MatchesMethod_GlobSemantics(string? pattern, string service, string method, bool expected)
    {
        var rule = new FaultRule { Method = pattern };
        Assert.Equal(expected, rule.MatchesMethod(service, method));
    }

    [Fact]
    public void FirstMatch_HonoursOrderAndEnabledFlag()
    {
        var set = FaultRuleSet.LoadJson("""
        {
          "rules": [
            { "method": "Svc/M", "enabled": false, "kind": "error", "errorStatusCode": 500 },
            { "method": "Svc/*", "kind": "error", "errorStatusCode": 502 },
            { "method": "*", "kind": "error", "errorStatusCode": 503 }
          ]
        }
        """);

        var hit = set.FirstMatch("Svc", "M");
        Assert.NotNull(hit);
        Assert.Equal(502, hit!.ErrorStatusCode); // disabled rule skipped, order wins over the catch-all
    }

    // ---- latency sampling (deterministic via canned uniforms) ----

    [Fact]
    public void SampleMs_Fixed_IgnoresUniformSource()
    {
        var latency = new FaultLatency { Distribution = "fixed", ValueMs = 250 };
        Assert.Equal(250, latency.SampleMs(() => 0.999));
    }

    [Fact]
    public void SampleMs_Uniform_MapsUnitIntervalOntoInclusiveRange()
    {
        var latency = new FaultLatency { Distribution = "uniform", MinMs = 100, MaxMs = 500 };
        Assert.Equal(100, latency.SampleMs(() => 0.0));
        Assert.Equal(500, latency.SampleMs(() => 0.999999));
        Assert.Equal(300, latency.SampleMs(() => 0.5));
    }

    [Fact]
    public void SampleMs_Exponential_MatchesClosedForm()
    {
        var latency = new FaultLatency { Distribution = "exponential", MeanMs = 100 };
        // -100 * ln(1 - 0.5) = 69.31…
        Assert.Equal(69, latency.SampleMs(() => 0.5));
        Assert.Equal(0, latency.SampleMs(() => 0.0));
    }

    [Fact]
    public void SampleMs_Normal_MeanWhenZIsZero()
    {
        var latency = new FaultLatency { Distribution = "normal", MeanMs = 200, StdDevMs = 50 };
        // u2 = 0.25 → cos(π/2) = 0 → z = 0 → mean.
        var queue = new Queue<double>([0.5, 0.25]);
        Assert.Equal(200, latency.SampleMs(() => queue.Dequeue()));
    }

    [Fact]
    public void SampleMs_ClampsToCapAndZero()
    {
        var exponential = new FaultLatency { Distribution = "exponential", MeanMs = FaultLatency.CapMs };
        Assert.Equal(FaultLatency.CapMs, exponential.SampleMs(() => 0.9999999999));

        var normal = new FaultLatency { Distribution = "normal", MeanMs = 10, StdDevMs = 1000 };
        // u2 = 0.5 → cos(π) = -1 → hugely negative sample → clamp 0.
        var queue = new Queue<double>([0.9, 0.5]);
        Assert.Equal(0, normal.SampleMs(() => queue.Dequeue()));
    }

    // ---- truncating wrapper ----

    [Fact]
    public async Task TruncatingStream_ForwardsCapThenSwallows()
    {
        using var sink = new MemoryStream();
        var aborted = 0;
        await using var wrapper = new TruncatingResponseStream(sink, capBytes: 4, abortConnection: () => aborted++);

        await wrapper.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        Assert.False(wrapper.Tripped);

        await wrapper.WriteAsync(new byte[] { 4, 5, 6 }, TestContext.Current.CancellationToken);
        Assert.True(wrapper.Tripped);
        Assert.Equal(1, aborted);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, sink.ToArray());

        // Post-trip writes are swallowed; the abort callback fires once.
        await wrapper.WriteAsync(new byte[] { 7 }, TestContext.Current.CancellationToken);
        Assert.Equal(1, aborted);
        Assert.Equal(4, sink.ToArray().Length);
    }

    // ---- end-to-end through a hosted mock ----

    [Fact]
    public async Task ErrorRule_RateOne_ShortCircuitsMatchedMethod()
    {
        using var host = BuildHost(SimpleRestRecording(), """
        { "rules": [ { "method": "Ping/*", "kind": "error", "errorStatusCode": 503 } ] }
        """);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/ping", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("fault", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorRule_OtherMethod_PassesThroughToReplayer()
    {
        using var host = BuildHost(SimpleRestRecording(), """
        { "rules": [ { "method": "OrderService/*", "kind": "error", "errorStatusCode": 503 } ] }
        """);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/ping", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("pong", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialResponseRule_TruncatesRecordedBody()
    {
        using var host = BuildHost(SimpleRestRecording(), """
        { "rules": [ { "kind": "partial-response", "partialBytes": 6 } ] }
        """);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/ping", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // Recorded body is {"message":"pong"} — only the first 6 bytes arrive.
        Assert.Equal("{\"mess", body);
    }

    [Fact]
    public async Task LatencyOnlyRule_StillServesFullResponse()
    {
        using var host = BuildHost(SimpleRestRecording(), """
        { "rules": [ { "kind": "latency-only", "latency": { "distribution": "fixed", "valueMs": 1 } } ] }
        """);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/ping", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("pong", body, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static BowireRecording SimpleRestRecording() => new()
    {
        Id = "rec_faults",
        Name = "faults",
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

    private static IHost BuildHost(BowireRecording recording, string faultsJson)
    {
        var faults = FaultRuleSet.LoadJson(faultsJson);
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
                            opts.Faults = faults;
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
