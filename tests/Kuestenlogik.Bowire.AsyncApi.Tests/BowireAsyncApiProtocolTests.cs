// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.AsyncApi;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// Skeleton-level sanity tests. The loader + binding resolvers land in
/// later commits — these tests just pin the plugin's identity, the
/// no-op discover shape, and that invoke surfaces a clear NotSupported
/// error until the loader exists.
/// </summary>
public sealed class BowireAsyncApiProtocolTests
{
    [Fact]
    public void Plugin_identity_is_stable()
    {
        var plugin = new BowireAsyncApiProtocol();
        Assert.Equal("asyncapi", plugin.Id);
        Assert.Equal("AsyncAPI", plugin.Name);
        Assert.NotEmpty(plugin.IconSvg);
    }

    [Fact]
    public async Task Discover_returns_empty_for_non_asyncapi_urls()
    {
        // gRPC / REST / MQTT URLs should produce zero services so the
        // discovery dispatcher's plugin probe doesn't pollute the
        // sidebar with empty AsyncAPI sections.
        var plugin = new BowireAsyncApiProtocol();
        var services = await plugin.DiscoverAsync(
            serverUrl: "https://api.example.com", showInternalServices: false,
            ct: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Empty(services);
    }

    [Fact]
    public async Task Discover_returns_empty_for_missing_file()
    {
        // File extension matches but the file doesn't exist — discover
        // must not throw, it should return empty so the dispatcher can
        // surface the IO error via its own try/catch rather than
        // propagating an unhandled exception.
        var plugin = new BowireAsyncApiProtocol();
        var services = await plugin.DiscoverAsync(
            serverUrl: "./does-not-exist.yaml", showInternalServices: false,
            ct: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Empty(services);
    }

    [Fact]
    public async Task Discover_loads_minimal_sample()
    {
        var plugin = new BowireAsyncApiProtocol();
        var sample = Path.Combine("TestData", "minimal.asyncapi.yaml");
        var services = await plugin.DiscoverAsync(
            serverUrl: sample, showInternalServices: false,
            ct: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        // Minimal sample has no operations, so the channel is discovered
        // but the methods list is empty — Bowire still surfaces the
        // service node so the topology is visible.
        Assert.Single(services);
        Assert.Equal("hello", services[0].Name);
        Assert.Equal("Minimal", services[0].Package);
        Assert.Empty(services[0].Methods);
    }

    [Fact]
    public async Task Discover_loads_smart_home_sample_with_operations_mapped()
    {
        var plugin = new BowireAsyncApiProtocol();
        var sample = Path.Combine("TestData", "smart-home-mqtt.asyncapi.yaml");

        var services = await plugin.DiscoverAsync(
            serverUrl: sample, showInternalServices: false,
            ct: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(2, services.Count);

        // Receive operation → server-streaming (broker → us)
        var measured = services.Single(s => s.Name == "lightingMeasured");
        Assert.Equal("Smart Home Hub", measured.Package);
        Assert.Equal("1.0.0", measured.Version);
        Assert.Equal("asyncapi", measured.Source);
        Assert.Equal(sample, measured.OriginUrl);
        var measuredOp = Assert.Single(measured.Methods);
        Assert.Equal("receiveLightMeasurement", measuredOp.Name);
        Assert.Equal("asyncapi-receive", measuredOp.MethodType);
        Assert.False(measuredOp.ClientStreaming);
        Assert.True(measuredOp.ServerStreaming);
        Assert.Equal("smarthome/light/measured", measuredOp.HttpPath);

        // Send operation → client-streaming (us → broker)
        var turnOnOff = services.Single(s => s.Name == "turnOnOff");
        var sendOp = Assert.Single(turnOnOff.Methods);
        Assert.Equal("sendTurnOnOff", sendOp.Name);
        Assert.Equal("asyncapi-send", sendOp.MethodType);
        Assert.True(sendOp.ClientStreaming);
        Assert.False(sendOp.ServerStreaming);
        Assert.Equal("smarthome/light/{room}/action", sendOp.HttpPath);
    }

    [Fact]
    public async Task Invoke_returns_error_when_document_was_never_discovered()
    {
        var plugin = new BowireAsyncApiProtocol();
        var result = await plugin.InvokeAsync(
            serverUrl: "./asyncapi.yaml",
            service: "light",
            method: "measured",
            jsonMessages: [],
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal("Error", result.Status);
        Assert.Contains("not been discovered", result.Metadata["error"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_returns_error_when_protocol_has_no_resolver()
    {
        // Discover first so the document is cached, then invoke an
        // operation against a server whose protocol has no resolver
        // registered (here: no Initialize, so the resolvers dict is
        // empty). Caller should get a clear "no resolver" message
        // pointing at the AsyncAPI roadmap.
        var plugin = new BowireAsyncApiProtocol();
        var sample = Path.Combine("TestData", "smart-home-mqtt.asyncapi.yaml");
        _ = await plugin.DiscoverAsync(sample, false, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var result = await plugin.InvokeAsync(
            serverUrl: sample,
            service: "turnOnOff",
            method: "sendTurnOnOff",
            jsonMessages: ["{\"command\":\"on\"}"],
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal("Error", result.Status);
        Assert.Contains("No AsyncAPI binding resolver", result.Metadata["error"], StringComparison.Ordinal);
    }
}
