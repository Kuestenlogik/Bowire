// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;
using Kuestenlogik.Bowire.Protocol.Pulsar;
using Xunit;

namespace Kuestenlogik.Bowire.Protocol.Pulsar.Tests;

/// <summary>
/// #664 — <c>/api/invoke</c> takes the method's discovery <c>name</c>;
/// the <c>fullName</c> is accepted too. Pulsar's invoke paths read a
/// route, so both forms have to arrive as one.
/// </summary>
public sealed class MethodNameContractTests
{
    [Theory]
    [InlineData("bowire-sample", "produce", "pulsar/topic/persistent://public/default/bowire-sample/produce")]
    [InlineData("bowire-sample", "subscribe", "pulsar/topic/persistent://public/default/bowire-sample/subscribe")]
    [InlineData("persistent://tenant/ns/orders", "produce", "pulsar/topic/persistent://tenant/ns/orders/produce")]
    [InlineData("bowire-sample", "pulsar/topic/persistent://public/default/bowire-sample/produce", "pulsar/topic/persistent://public/default/bowire-sample/produce")]
    [InlineData("bowire-sample", "somethingElse", "somethingElse")]
    public void Name_And_FullName_Both_Resolve_To_The_Route(string service, string sent, string expected)
    {
        using var p = new BowirePulsarProtocol();
        Assert.Equal(expected, p.ResolveMethodName(service, sent));
    }

    [Fact]
    public void Short_Topic_Is_Completed_In_The_Users_Configured_Namespace()
    {
        // #843 — the workspace's "namespaces" setting, not the declared
        // default, decides where a short topic name lives.
        using var p = new BowirePulsarProtocol();
        p.Initialize(new SettingsOnlyServiceProvider(new FixedPluginSettings(new()
        {
            ["namespaces"] = "acme/prod, acme/staging",
        })));

        Assert.Equal("pulsar/topic/persistent://acme/prod/orders/produce", p.ResolveMethodName("orders", "produce"));
    }

    private sealed class SettingsOnlyServiceProvider(IBowirePluginSettings settings) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IBowirePluginSettings) ? settings : null;
    }

    private sealed class FixedPluginSettings(Dictionary<string, string> values) : IBowirePluginSettings
    {
        public string? GetValue(string pluginId, string key)
            => pluginId == "pulsar" && values.TryGetValue(key, out var v) ? v : null;
    }
}
