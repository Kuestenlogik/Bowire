// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Mqtt;
using Kuestenlogik.Bowire.Protocol.OData;
using Kuestenlogik.Bowire.Protocol.SocketIo;
using Xunit;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Discovery smoke tests for the new protocol plugins (MQTT, Socket.IO, OData).
/// These tests verify that the plugins instantiate correctly and return empty
/// results for invalid/unreachable URLs without crashing. Live-server tests
/// require external infrastructure and are covered by the E2E samples.
/// </summary>
public sealed class NewProtocolDiscoveryTests
{
    [Fact]
    public void MqttProtocol_HasCorrectMetadata()
    {
        var proto = new BowireMqttProtocol();
        Assert.Equal("MQTT", proto.Name);
        Assert.Equal("mqtt", proto.Id);
        Assert.NotEmpty(proto.IconSvg);
    }

    [Fact]
    public async Task MqttProtocol_EmptyUrl_ReturnsEmpty()
    {
        var proto = new BowireMqttProtocol();
        var result = await proto.DiscoverAsync("", false, TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task MqttProtocol_InvalidUrl_ReturnsEmpty()
    {
        var proto = new BowireMqttProtocol();
        // Non-MQTT URL should return empty, not throw
        var result = await proto.DiscoverAsync("https://example.com", false, TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public void SocketIoProtocol_HasCorrectMetadata()
    {
        var proto = new BowireSocketIoProtocol();
        Assert.Equal("Socket.IO", proto.Name);
        Assert.Equal("socketio", proto.Id);
        Assert.NotEmpty(proto.IconSvg);
    }

    [Fact]
    public async Task SocketIoProtocol_EmptyUrl_ReturnsEmpty()
    {
        var proto = new BowireSocketIoProtocol();
        var result = await proto.DiscoverAsync("", false, TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task SocketIoProtocol_NonSocketIoUrl_ReturnsEmpty()
    {
        var proto = new BowireSocketIoProtocol();
        // Non-HTTP URL should return empty
        var result = await proto.DiscoverAsync("mqtt://localhost:1883", false, TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public void ODataProtocol_HasCorrectMetadata()
    {
        var proto = new BowireODataProtocol();
        Assert.Equal("OData", proto.Name);
        Assert.Equal("odata", proto.Id);
        Assert.NotEmpty(proto.IconSvg);
    }

    [Fact]
    public async Task ODataProtocol_EmptyUrl_ReturnsEmpty()
    {
        var proto = new BowireODataProtocol();
        var result = await proto.DiscoverAsync("", false, TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ODataProtocol_NonODataUrl_ReturnsEmpty()
    {
        var proto = new BowireODataProtocol();
        // Non-HTTP URL should return empty
        var result = await proto.DiscoverAsync("mqtt://localhost:1883", false, TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public void MqttConnectionHelper_ParsesBrokerUrls()
    {
        Assert.Equal(("localhost", 1883), MqttConnectionHelper.ParseBrokerUrl("mqtt://localhost:1883"));
        Assert.Equal(("broker.io", 8883), MqttConnectionHelper.ParseBrokerUrl("mqtts://broker.io:8883"));
        Assert.Equal(("localhost", 1883), MqttConnectionHelper.ParseBrokerUrl("localhost"));
        Assert.Null(MqttConnectionHelper.ParseBrokerUrl(""));
    }

    [Fact]
    public void MqttPayloadHelper_HandlesJson()
    {
        var json = System.Text.Encoding.UTF8.GetBytes("{\"value\": 42}");
        var result = MqttPayloadHelper.PayloadToDisplayString(json);
        Assert.Contains("42", result);
    }

    [Fact]
    public void MqttPayloadHelper_HandlesText()
    {
        var text = System.Text.Encoding.UTF8.GetBytes("hello world");
        var result = MqttPayloadHelper.PayloadToDisplayString(text);
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void MqttPayloadHelper_HandlesBinary()
    {
        var binary = new byte[] { 0xFF, 0xFE, 0x00, 0x01 };
        var result = MqttPayloadHelper.PayloadToDisplayString(binary);
        Assert.Contains("[binary:", result);
        Assert.Contains("FF", result);
    }
}
