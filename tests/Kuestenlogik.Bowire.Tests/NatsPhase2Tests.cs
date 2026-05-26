// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Protocol.Nats;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Phase-2-specific helper tests. The live JetStream + Services
/// integration suite needs a running nats-server with <c>-js</c>
/// (Testcontainers — Phase 2 next step); these tests stay
/// network-free and cover the URL-route parser the dispatch logic
/// branches on plus the JetStream / Services discovery output
/// shape.
/// </summary>
public sealed class NatsPhase2Tests
{
    private static readonly Type s_protocolType = typeof(BowireNatsProtocol);

    // ----- ParseRoute -------------------------------------------------

    [Theory]
    [InlineData("nats/jetstream/orders/info",            "JetStream", "",                "info",    "orders",  null)]
    [InlineData("nats/jetstream/orders/consume",         "JetStream", "",                "consume", "orders",  null)]
    [InlineData("nats/jetstream/orders/publish/foo.bar", "JetStream", "foo.bar",         "publish", "orders",  null)]
    [InlineData("nats/services/echo/say",                "Services",  "say",             "request", null,      "echo")]
    [InlineData("nats/services/echo/some.subject",       "Services",  "some.subject",    "request", null,      "echo")]
    [InlineData("nats/health/publish",                   "Core",      "health",          "publish", null,      null)]
    [InlineData("nats/orders.created/request",           "Core",      "orders.created",  "request", null,      null)]
    [InlineData("health",                                "Core",      "health",          "publish", null,      null)]
    [InlineData("",                                      "Core",      "",                "publish", null,      null)]
    public void ParseRoute_Splits_All_Families(
        string input, string expectedFamily, string expectedSubject,
        string expectedOp, string? expectedStream, string? expectedService)
    {
        var route = InvokeParseRoute(input);
        // Route is a private readonly record struct. Reflect each
        // property by name so the test stays robust to constructor-
        // parameter shuffles.
        Assert.Equal(expectedFamily, GetMember(route, "Family")?.ToString());
        Assert.Equal(expectedSubject, GetMember(route, "Subject"));
        Assert.Equal(expectedOp, GetMember(route, "Op"));
        Assert.Equal(expectedStream, GetMember(route, "StreamName"));
        Assert.Equal(expectedService, GetMember(route, "ServiceName"));
    }

    [Fact]
    public void ParseRoute_Malformed_JetStream_Prefix_Falls_Back_To_Core()
    {
        // 'nats/jetstream' without a stream name + slash — there's no
        // second segment to read, so the parser falls back to the
        // Phase-1 (subject, op) shape rather than fabricating a
        // bogus JetStream route.
        var route = InvokeParseRoute("nats/jetstream");
        Assert.Equal("Core", GetMember(route, "Family")?.ToString());
    }

    [Fact]
    public void ParseRoute_Malformed_Services_Prefix_Falls_Back_To_Core()
    {
        var route = InvokeParseRoute("nats/services");
        Assert.Equal("Core", GetMember(route, "Family")?.ToString());
    }

    // ----- Discovery building blocks ---------------------------------

    [Fact]
    public void Protocol_Surface_Still_Identifies_As_Nats_Across_Phases()
    {
        // Sanity check: Phase 2 additions don't change the plugin's
        // public identity. The host (BowireProtocolRegistry) keys on
        // these and a silent rename would break every connected
        // workbench session.
        var p = new BowireNatsProtocol();
        Assert.Equal("nats", p.Id);
        Assert.Equal("NATS", p.Name);
    }

    [Fact]
    public async Task DiscoverAsync_With_Unreachable_Server_Returns_Empty()
    {
        // Same contract as Phase 1: hitting a port nobody listens on
        // returns an empty list (catch swallows the connect failure)
        // rather than throwing. Holds for Phase 2's three-way
        // discovery because each source is wrapped in its own
        // best-effort catch.
        var p = new BowireNatsProtocol();
        var result = await p.DiscoverAsync(
            "nats://127.0.0.1:1",
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task InvokeAsync_JetStream_Info_On_Unreachable_Returns_Error()
    {
        var p = new BowireNatsProtocol();
        var result = await p.InvokeAsync(
            "nats://127.0.0.1:1",
            service: "JetStream:orders",
            method: "nats/jetstream/orders/info",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.NotEqual("OK", result.Status);
    }

    [Fact]
    public async Task InvokeAsync_Service_Endpoint_On_Unreachable_Returns_Error()
    {
        var p = new BowireNatsProtocol();
        var result = await p.InvokeAsync(
            "nats://127.0.0.1:1",
            service: "Service:echo",
            method: "nats/services/echo/say",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.NotEqual("OK", result.Status);
    }

    // ----- reflection helpers ----------------------------------------

    private static object InvokeParseRoute(string method)
    {
        var mi = s_protocolType.GetMethod(
            "ParseRoute", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ParseRoute not found");
        return mi.Invoke(null, [method])
            ?? throw new InvalidOperationException("ParseRoute returned null");
    }

    private static object? GetMember(object obj, string name)
    {
        var t = obj.GetType();
        var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop is not null) return prop.GetValue(obj);
        var field = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(obj);
    }
}
