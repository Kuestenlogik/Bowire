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
    public async Task Discover_returns_empty_until_loader_is_wired()
    {
        var plugin = new BowireAsyncApiProtocol();
        var services = await plugin.DiscoverAsync(
            serverUrl: "./asyncapi.yaml", showInternalServices: false,
            ct: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Empty(services);
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
