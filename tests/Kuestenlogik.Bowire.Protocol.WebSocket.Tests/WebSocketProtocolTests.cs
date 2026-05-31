// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.WebSocket;

namespace Kuestenlogik.Bowire.Protocol.WebSocket.Tests;

/// <summary>
/// Tests for the WebSocket protocol plugin's discovery surface — specifically
/// the standalone "ad-hoc" path that synthesises a single-method service when
/// the user passes <c>bowire --url ws://...</c> against an arbitrary remote
/// endpoint without any prior registration.
/// </summary>
[Collection("RegisteredEndpointsSerialised")]
public sealed class WebSocketProtocolTests : IDisposable
{
    public WebSocketProtocolTests()
    {
        BowireWebSocketProtocol.ClearRegisteredEndpoints();
    }

    public void Dispose()
    {
        BowireWebSocketProtocol.ClearRegisteredEndpoints();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DiscoverAsync_AdHocWebSocketUrl_ReturnsSingleDuplexMethod()
    {
        var protocol = new BowireWebSocketProtocol();
        var services = await protocol.DiscoverAsync("ws://example.com/ws/echo", showInternalServices: false, TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        Assert.Equal("WebSocket", svc.Name);
        Assert.Equal("websocket", svc.Source);
        Assert.Equal("ws://example.com/ws/echo", svc.OriginUrl);

        var method = Assert.Single(svc.Methods);
        Assert.Equal("/ws/echo", method.Name);
        Assert.Equal("Duplex", method.MethodType);
        Assert.True(method.ClientStreaming);
        Assert.True(method.ServerStreaming);
    }

    [Fact]
    public void WebSocketEndpointAttribute_DefaultsAreNull_AndPropertiesRoundTrip()
    {
        // Zero-arg ctor — both display name and description default to null.
        var empty = new WebSocketEndpointAttribute();
        Assert.Null(empty.DisplayName);
        Assert.Null(empty.Description);

        // Positional args — values land on the corresponding properties.
        var attr = new WebSocketEndpointAttribute("Echo", "Echoes every message back.");
        Assert.Equal("Echo", attr.DisplayName);
        Assert.Equal("Echoes every message back.", attr.Description);
    }

    [Fact]
    public void WebSocketGroupAttribute_NamePropertyRoundTrips()
    {
        var attr = new WebSocketGroupAttribute("Chat");
        Assert.Equal("Chat", attr.Name);
    }

    [Fact]
    public async Task DiscoverAsync_NonWebSocketUrl_ReturnsEmpty()
    {
        var protocol = new BowireWebSocketProtocol();
        var services = await protocol.DiscoverAsync("http://example.com/api", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_RegisteredEndpoints_ProduceWebSocketService()
    {
        BowireWebSocketProtocol.RegisterEndpoint(new WebSocketEndpointInfo("/ws/chat", "Chat", "Group chat"));

        var protocol = new BowireWebSocketProtocol();
        var services = await protocol.DiscoverAsync("http://localhost:5000", showInternalServices: false, TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        var method = Assert.Single(svc.Methods);
        Assert.Equal("/ws/chat", method.Name);
        Assert.Equal("Chat", method.Summary);
        Assert.Equal("Group chat", method.Description);
    }
}
