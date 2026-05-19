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
        Assert.Single(services);
        Assert.Equal("hello", services[0].Name);
        Assert.Equal("Minimal", services[0].Package);
    }

    [Fact]
    public async Task Discover_loads_smart_home_sample_and_maps_two_channels()
    {
        var plugin = new BowireAsyncApiProtocol();
        var sample = Path.Combine("TestData", "smart-home-mqtt.asyncapi.yaml");

        var services = await plugin.DiscoverAsync(
            serverUrl: sample, showInternalServices: false,
            ct: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(2, services.Count);

        var measured = services.Single(s => s.Name == "lightingMeasured");
        Assert.Equal("Smart Home Hub", measured.Package);
        Assert.Equal("1.0.0", measured.Version);
        Assert.Equal("asyncapi", measured.Source);
        Assert.Equal(sample, measured.OriginUrl);
        Assert.Single(measured.Methods);
        Assert.Equal("smarthome/light/measured", measured.Methods[0].Name);
        Assert.Equal("asyncapi-channel", measured.Methods[0].MethodType);

        var turnOnOff = services.Single(s => s.Name == "turnOnOff");
        Assert.Equal("smarthome/light/{room}/action", turnOnOff.Methods[0].Name);
    }

    [Fact]
    public async Task Invoke_throws_NotSupported_until_loader_is_wired()
    {
        var plugin = new BowireAsyncApiProtocol();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            plugin.InvokeAsync(
                serverUrl: "./asyncapi.yaml",
                service: "light",
                method: "measured",
                jsonMessages: [],
                showInternalServices: false,
                ct: TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
    }
}
