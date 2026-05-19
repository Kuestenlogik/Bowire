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
    public async Task Invoke_routes_v2_operation_to_protocol_resolver_lookup()
    {
        // Without a registered MQTT resolver the V2 invoke path still
        // has to: (1) recognise the URL was discovered as V2, (2) find
        // the channel by `service` parameter, (3) match the requested
        // method against publish/subscribe operationId, (4) reject
        // with the "no resolver" error rather than the
        // "not discovered" one. Proves the V2 dispatch landed.
        var plugin = new BowireAsyncApiProtocol();
        var sample = Path.Combine("TestData", "v2-smart-home.asyncapi.yaml");
        _ = await plugin.DiscoverAsync(sample, false, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var result = await plugin.InvokeAsync(
            serverUrl: sample,
            service: "smarthome/light/{room}/action",
            method: "sendTurnOnOff",
            jsonMessages: ["{}"],
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal("Error", result.Status);
        Assert.Contains("No AsyncAPI binding resolver", result.Metadata["error"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_rejects_v2_method_that_no_channel_slot_matches()
    {
        var plugin = new BowireAsyncApiProtocol();
        var sample = Path.Combine("TestData", "v2-smart-home.asyncapi.yaml");
        _ = await plugin.DiscoverAsync(sample, false, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var result = await plugin.InvokeAsync(
            serverUrl: sample,
            service: "smarthome/light/{room}/action",
            method: "nonExistentOp",
            jsonMessages: ["{}"],
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal("Error", result.Status);
        Assert.Contains("not found on V2 channel", result.Metadata["error"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_maps_v2_document_with_publish_and_subscribe()
    {
        var plugin = new BowireAsyncApiProtocol();
        var sample = Path.Combine("TestData", "v2-smart-home.asyncapi.yaml");
        var services = await plugin.DiscoverAsync(
            serverUrl: sample, showInternalServices: false,
            ct: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(2, services.Count);
        Assert.All(services, s => Assert.Equal("asyncapi", s.Source));
        Assert.All(services, s => Assert.Equal("Smart Home Hub (V2)", s.Package));

        // The subscribe channel exposes a receive-direction method:
        // operationId "receiveLightMeasurement" with ServerStreaming
        // semantics (broker → us).
        var measured = services.Single(s => s.Name == "smarthome/light/measured");
        var receiveOp = Assert.Single(measured.Methods);
        Assert.Equal("receiveLightMeasurement", receiveOp.Name);
        Assert.Equal("asyncapi-receive", receiveOp.MethodType);
        Assert.False(receiveOp.ClientStreaming);
        Assert.True(receiveOp.ServerStreaming);
        Assert.Equal("smarthome/light/measured", receiveOp.HttpPath);

        // The publish channel exposes a send-direction method.
        var turnOnOff = services.Single(s => s.Name == "smarthome/light/{room}/action");
        var sendOp = Assert.Single(turnOnOff.Methods);
        Assert.Equal("sendTurnOnOff", sendOp.Name);
        Assert.Equal("asyncapi-send", sendOp.MethodType);
        Assert.True(sendOp.ClientStreaming);
        Assert.False(sendOp.ServerStreaming);
        Assert.Equal("smarthome/light/{room}/action", sendOp.HttpPath);
    }

    [Fact]
    public async Task Discover_preserves_utf8_em_dash_in_title()
    {
        var plugin = new BowireAsyncApiProtocol();
        var sample = Path.Combine("TestData", "utf8-mojibake-probe.asyncapi.yaml");
        var services = await plugin.DiscoverAsync(
            serverUrl: sample, showInternalServices: false,
            ct: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        // Title goes into BowireServiceInfo.Package via document.Info.Title.
        // Em-dash should land intact, not turn into the mojibake triple `â€"`.
        Assert.Single(services);
        Assert.Equal("Harbor Control Center — Event Stream", services[0].Package);
    }

    [Fact]
    public async Task Discover_handles_unquoted_versions_via_prenormaliser()
    {
        // asyncapi/net-sdk#76 — without the pre-normaliser this throws.
        // The sample has `asyncapi: 3.0.0` and `info.version: 1.2.3`
        // both unquoted; discover should still succeed.
        var plugin = new BowireAsyncApiProtocol();
        var sample = Path.Combine("TestData", "unquoted-versions.asyncapi.yaml");
        var services = await plugin.DiscoverAsync(
            serverUrl: sample, showInternalServices: false,
            ct: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Single(services);
        Assert.Equal("ping", services[0].Name);
        Assert.Equal("1.2.3", services[0].Version);
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
