// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Plugins;
using Kuestenlogik.Bowire.Plugins.Sidecar;
using Xunit;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// A sidecar's declared settings reach the Settings dialog, and the values
/// set there reach the sidecar (#693). Before this, three SDKs implemented
/// a settings hook, the JSON went over the wire, and the host dropped it.
/// </summary>
public class SidecarSettingsTests
{
    private static SidecarBowireProtocol BuildPlugin(
        bool advertiseSettings, IBowirePluginSettings? values = null)
    {
        var exe = SidecarFake.Locate();
        var manifest = new SidecarPluginManifest(
            PackageId: "Kuestenlogik.Bowire.Tests.SidecarFake",
            Protocol: new SidecarProtocolMetadata("fake", "Fake"),
            Executable: Path.GetFileName(exe),
            Args: advertiseSettings ? ["--settings"] : null,
            EnvPrefix: "BOWIRE_FAKE_",
            ShutdownTimeoutMs: 2000);

        var plugin = new SidecarBowireProtocol(manifest, Path.GetDirectoryName(exe)!);
        plugin.Initialize(values is null ? null : new SingleServiceProvider(values));
        return plugin;
    }

    [Fact]
    public async Task A_Sidecar_Advertising_Two_Settings_Surfaces_Both()
    {
        var plugin = BuildPlugin(advertiseSettings: true);
        try
        {
            await plugin.PrepareSettingsAsync(TestContext.Current.CancellationToken);

            Assert.Collection(plugin.Settings,
                first =>
                {
                    Assert.Equal("probeWindow", first.Key);
                    Assert.Equal("Probe window", first.Label);
                    Assert.Equal("number", first.Type);
                    Assert.Equal(5, ((JsonElement)first.DefaultValue!).GetInt32());
                },
                second =>
                {
                    Assert.Equal("verbose", second.Key);
                    Assert.Equal("Verbose", second.Label);
                    Assert.Equal("Log every frame", second.Description);
                    Assert.Equal("bool", second.Type);
                });
        }
        finally
        {
            await SidecarFake.ShutdownAsync(plugin);
        }
    }

    [Fact]
    public async Task A_Sidecar_Advertising_None_Surfaces_None()
    {
        var plugin = BuildPlugin(advertiseSettings: false);
        try
        {
            await plugin.PrepareSettingsAsync(TestContext.Current.CancellationToken);

            Assert.Empty(plugin.Settings);
        }
        finally
        {
            await SidecarFake.ShutdownAsync(plugin);
        }
    }

    [Fact]
    public void Settings_Are_Empty_Before_The_Handshake()
    {
        // The declaration lives in a process that has not been started, so
        // the synchronous property has nothing truthful to say yet. This is
        // why IBowireDeferredSettings exists.
        var plugin = BuildPlugin(advertiseSettings: true);

        Assert.Empty(plugin.Settings);
    }

    [Fact]
    public async Task A_Value_Set_For_A_Declared_Setting_Reaches_The_Sidecar()
    {
        var values = new StubPluginSettings { ["fake/probeWindow"] = "42" };
        var plugin = BuildPlugin(advertiseSettings: true, values);
        try
        {
            await plugin.PrepareSettingsAsync(TestContext.Current.CancellationToken);

            var result = await plugin.InvokeAsync(
                serverUrl: "fake://demo",
                service: "Echo",
                method: "Echo/echo",
                jsonMessages: ["{}"],
                showInternalServices: false,
                ct: TestContext.Current.CancellationToken);

            // The fake hands back whatever arrived in params.settings.
            var echoed = result.Metadata["settings"];
            Assert.Contains("\"probeWindow\":\"42\"", echoed, StringComparison.Ordinal);
            // Only keys that are set travel: the sidecar declared the
            // defaults and still knows them.
            Assert.DoesNotContain("verbose", echoed, StringComparison.Ordinal);
        }
        finally
        {
            await SidecarFake.ShutdownAsync(plugin);
        }
    }

    [Fact]
    public async Task A_Changed_Value_Reaches_The_Sidecar_Without_A_Restart()
    {
        // The property a .NET plugin gets by asking the store when it needs
        // a value. A sidecar cannot ask, so the host reads afresh per call
        // rather than caching at spawn — otherwise changing a setting would
        // mean restarting the process.
        var values = new StubPluginSettings { ["fake/probeWindow"] = "42" };
        var plugin = BuildPlugin(advertiseSettings: true, values);
        try
        {
            await plugin.PrepareSettingsAsync(TestContext.Current.CancellationToken);
            await InvokeAsync(plugin);

            values["fake/probeWindow"] = "7";
            var after = await InvokeAsync(plugin);

            Assert.Contains("\"probeWindow\":\"7\"", after, StringComparison.Ordinal);
        }
        finally
        {
            await SidecarFake.ShutdownAsync(plugin);
        }

        static async Task<string> InvokeAsync(SidecarBowireProtocol plugin)
        {
            var result = await plugin.InvokeAsync(
                serverUrl: "fake://demo",
                service: "Echo",
                method: "Echo/echo",
                jsonMessages: ["{}"],
                showInternalServices: false,
                ct: TestContext.Current.CancellationToken);
            return result.Metadata["settings"];
        }
    }

    [Fact]
    public async Task A_Sidecar_With_No_Values_Set_Sends_No_Settings_At_All()
    {
        var plugin = BuildPlugin(advertiseSettings: true, new StubPluginSettings());
        try
        {
            await plugin.PrepareSettingsAsync(TestContext.Current.CancellationToken);

            var result = await plugin.InvokeAsync(
                serverUrl: "fake://demo",
                service: "Echo",
                method: "Echo/echo",
                jsonMessages: ["{}"],
                showInternalServices: false,
                ct: TestContext.Current.CancellationToken);

            // WhenWritingNull drops the field entirely, so a sidecar that
            // never had settings sees exactly the envelope it saw before.
            Assert.Equal("", result.Metadata["settings"]);
        }
        finally
        {
            await SidecarFake.ShutdownAsync(plugin);
        }
    }

    /// <summary>Values keyed as <c>pluginId/key</c>, mutable so a test can change one mid-flight.</summary>
    private sealed class StubPluginSettings : Dictionary<string, string>, IBowirePluginSettings
    {
        public StubPluginSettings() : base(StringComparer.Ordinal) { }

        public string? GetValue(string pluginId, string key)
            => TryGetValue(pluginId + "/" + key, out var value) ? value : null;
    }

    private sealed class SingleServiceProvider(IBowirePluginSettings settings) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IBowirePluginSettings) ? settings : null;
    }
}
