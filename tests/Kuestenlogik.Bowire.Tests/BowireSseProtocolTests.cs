// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Sse;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the SSE protocol plugin's identity, discovery surface, and
/// streaming-only invocation guard. The live SSE-server test path
/// (<c>InvokeStreamAsync</c> / <c>SubscribeAsync</c>) is integration-tier
/// territory — covered by Kuestenlogik.Bowire.IntegrationTests, not here.
/// </summary>
public sealed class BowireSseProtocolTests : IDisposable
{
    public BowireSseProtocolTests()
    {
        BowireSseProtocol.ClearRegisteredEndpoints();
    }

    public void Dispose()
    {
        BowireSseProtocol.ClearRegisteredEndpoints();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Identity_Properties_Are_Stable()
    {
        var protocol = new BowireSseProtocol();

        Assert.Equal("SSE", protocol.Name);
        Assert.Equal("sse", protocol.Id);
        Assert.NotNull(protocol.IconSvg);
        Assert.Contains("<svg", protocol.IconSvg, StringComparison.Ordinal);
    }

    [Fact]
    public void Implements_Protocol_And_Inline_Sse_Subscriber_Surfaces()
    {
        var protocol = new BowireSseProtocol();

        Assert.IsAssignableFrom<IBowireProtocol>(protocol);
        Assert.IsAssignableFrom<IInlineSseSubscriber>(protocol);
    }

    [Fact]
    public void Initialize_Accepts_Null_Service_Provider()
    {
        var protocol = new BowireSseProtocol();

        // Standalone tool mode passes null — should not throw.
        protocol.Initialize(null);
    }

    [Fact]
    public async Task DiscoverAsync_With_No_Registrations_Returns_Empty()
    {
        var protocol = new BowireSseProtocol();

        var services = await protocol.DiscoverAsync(
            "http://localhost:5000",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_With_Registered_Endpoint_Produces_Single_ServerStreaming_Method()
    {
        BowireSseProtocol.RegisterEndpoint(
            new SseEndpointInfo("/events/ticker", "Ticker", "Live ticks", "price,volume"));

        var protocol = new BowireSseProtocol();
        var services = await protocol.DiscoverAsync(
            "http://localhost:5000",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        Assert.Equal("SSE Endpoints", svc.Name);
        Assert.Equal("/events", svc.Package);
        Assert.Equal("sse", svc.Source);

        var method = Assert.Single(svc.Methods);
        Assert.Equal("Ticker", method.Name);
        Assert.Equal("SSE/events/ticker", method.FullName);
        Assert.False(method.ClientStreaming);
        Assert.True(method.ServerStreaming);
        Assert.Equal("ServerStreaming", method.MethodType);
    }

    [Fact]
    public async Task DiscoverAsync_With_Multiple_Endpoints_Lists_Each_Method()
    {
        BowireSseProtocol.RegisterEndpoint(new SseEndpointInfo("/events/a", "A"));
        BowireSseProtocol.RegisterEndpoint(new SseEndpointInfo("/events/b", "B"));
        BowireSseProtocol.RegisterEndpoint(new SseEndpointInfo("/events/c", "C"));

        var protocol = new BowireSseProtocol();
        var services = await protocol.DiscoverAsync(
            "http://localhost:5000",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        Assert.Equal(3, svc.Methods.Count);
        Assert.Equal(["A", "B", "C"], svc.Methods.Select(m => m.Name).ToArray());
    }

    [Fact]
    public async Task DiscoverAsync_Deduplicates_Same_Path()
    {
        BowireSseProtocol.RegisterEndpoint(new SseEndpointInfo("/events/dup", "First"));
        BowireSseProtocol.RegisterEndpoint(new SseEndpointInfo("/events/dup", "Second"));

        var protocol = new BowireSseProtocol();
        var services = await protocol.DiscoverAsync(
            "http://localhost:5000",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        var method = Assert.Single(svc.Methods);
        // First registration wins per dedup-by-path rule.
        Assert.Equal("First", method.Name);
    }

    [Fact]
    public async Task DiscoverAsync_Output_Schema_Has_Standard_Sse_Fields()
    {
        BowireSseProtocol.RegisterEndpoint(new SseEndpointInfo("/events/x", "X"));

        var protocol = new BowireSseProtocol();
        var services = await protocol.DiscoverAsync(
            "http://localhost:5000",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        var method = services[0].Methods[0];
        Assert.Equal("SseEvent", method.OutputType.Name);
        Assert.Equal(["id", "event", "data", "retry"],
            method.OutputType.Fields.Select(f => f.Name).ToArray());

        Assert.Equal("SubscribeRequest", method.InputType.Name);
        var urlField = Assert.Single(method.InputType.Fields);
        Assert.Equal("url", urlField.Name);
    }

    [Fact]
    public async Task InvokeAsync_Returns_Streaming_Only_Sentinel()
    {
        var protocol = new BowireSseProtocol();
        var result = await protocol.InvokeAsync(
            "http://localhost:5000",
            "SSE Endpoints",
            "SSE/events/x",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.Null(result.Response);
        Assert.Equal(0, result.DurationMs);
        Assert.Contains("streaming only", result.Status, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Metadata);
        Assert.Empty(result.Metadata);
    }

    [Fact]
    public async Task OpenChannelAsync_Returns_Null_Because_Sse_Is_Unidirectional()
    {
        var protocol = new BowireSseProtocol();

        var channel = await protocol.OpenChannelAsync(
            "http://localhost:5000",
            "SSE Endpoints",
            "SSE/events/x",
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken);

        Assert.Null(channel);
    }

    [Fact]
    public void RegisterEndpoint_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BowireSseProtocol.RegisterEndpoint(null!));
    }

    [Fact]
    public void RegisteredEndpoints_Reflects_Registrations_And_Clear()
    {
        BowireSseProtocol.RegisterEndpoint(new SseEndpointInfo("/a", "A"));
        BowireSseProtocol.RegisterEndpoint(new SseEndpointInfo("/b", "B"));

        Assert.Equal(2, BowireSseProtocol.RegisteredEndpoints.Count);

        BowireSseProtocol.ClearRegisteredEndpoints();

        Assert.Empty(BowireSseProtocol.RegisteredEndpoints);
    }
}
