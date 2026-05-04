// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.PluginLoading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for the public DI surface of <see cref="BowireServiceCollectionExtensions"/>.
/// The class is the canonical Program.cs wiring path: <c>AddBowire()</c>
/// scans for <see cref="IBowireProtocolServices"/> implementations and
/// <c>AddBowirePlugins(...)</c> loads out-of-tree plugin DLLs from a
/// directory. Existing tests in <c>PluginLoadingTests</c> + the
/// <c>IConfiguration</c> overload tests in <c>BowireConfigurationTests</c>
/// cover guard branches; this class fills in the loaded-assembly scanning
/// path and the loose-DLLs-at-root branch that wasn't otherwise reached.
/// </summary>
public class BowireServiceCollectionExtensionsTests
{
    [Fact]
    public void AddBowire_Returns_Same_Service_Collection_For_Chaining()
    {
        var services = new ServiceCollection();
        var result = services.AddBowire();

        Assert.Same(services, result);
    }

    [Fact]
    public void AddBowire_Registers_Protocol_Services_From_Loaded_Assemblies()
    {
        // The test project references the gRPC, REST, SignalR, ... protocol
        // plugins so AddBowire must invoke their IBowireProtocolServices
        // hooks. Confirm it added at least one service descriptor — every
        // plugin we reference contributes at minimum one service registration
        // (e.g. AddGrpc -> hundreds; even REST contributes options).
        var services = new ServiceCollection();
        var initialCount = services.Count;

        services.AddBowire();

        Assert.NotEqual(initialCount, services.Count);
    }

    [Fact]
    public void AddBowire_Is_Idempotent_On_Repeat_Calls()
    {
        // Calling AddBowire twice must not crash — plugin
        // ConfigureServices implementations are documented as idempotent.
        var services = new ServiceCollection();

        services.AddBowire();
        var ex = Record.Exception(() => services.AddBowire());

        Assert.Null(ex);
    }

    [Fact]
    public void AddBowirePlugins_With_Subdirectories_Registers_Plugin_Host()
    {
        // A plugin dir with package subdirectories (the canonical layout
        // produced by `bowire plugin install`) — even with no DLLs inside
        // each subdir, the plugin host gets registered so embedded hosts
        // can later hot-reload from disk. The TryLoadPlugin call inside
        // each subdir swallows failures, so empty subdirs are tolerated.
        var temp = Path.Combine(Path.GetTempPath(),
            "bowire-plugin-test-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(temp, "package-a");
        Directory.CreateDirectory(sub);
        try
        {
            var services = new ServiceCollection();
            services.AddBowirePlugins(temp);

            // The host is added as a singleton instance — find it.
            var descriptor = services.FirstOrDefault(d =>
                d.ServiceType == typeof(BowirePluginHost));
            Assert.NotNull(descriptor);
            Assert.NotNull(descriptor!.ImplementationInstance);
            Assert.IsType<BowirePluginHost>(descriptor.ImplementationInstance);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void AddBowirePlugins_Reuses_Existing_Plugin_Host_On_Second_Call()
    {
        // The internal GetOrAddPluginHost helper looks up the existing
        // descriptor before allocating a fresh host so a second
        // AddBowirePlugins call (e.g. from a different config root) doesn't
        // shadow the first one's loaded contexts.
        var temp = Path.Combine(Path.GetTempPath(),
            "bowire-plugin-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "first"));
        Directory.CreateDirectory(Path.Combine(temp, "second"));
        try
        {
            var services = new ServiceCollection();
            services.AddBowirePlugins(temp);
            services.AddBowirePlugins(temp);

            var hosts = services
                .Where(d => d.ServiceType == typeof(BowirePluginHost))
                .ToList();
            Assert.Single(hosts);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void AddBowirePlugins_Whitespace_Path_Is_NoOp()
    {
        // string.IsNullOrWhiteSpace short-circuits before path resolution
        // so a "    " input must not throw and must not register a host.
        var services = new ServiceCollection();
        var result = services.AddBowirePlugins("   ");

        Assert.Same(services, result);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(BowirePluginHost));
    }

    [Fact]
    public void AddBowirePlugins_IConfiguration_Whitespace_Value_Is_NoOp()
    {
        // The IConfiguration overload reads "Bowire:PluginDir"; a
        // whitespace-only value must behave like an unset key — no host
        // gets registered, no exception.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:PluginDir"] = "   "
            })
            .Build();
        var services = new ServiceCollection();

        var result = services.AddBowirePlugins(config);

        Assert.Same(services, result);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(BowirePluginHost));
    }

    [Fact]
    public void AddBowirePlugins_Null_IConfiguration_Throws_ArgumentNullException()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => services.AddBowirePlugins((IConfiguration)null!));
    }

    [Fact]
    public void AddBowirePlugins_With_Loose_Dll_At_Root_Triggers_Root_TryLoad()
    {
        // The "loose DLLs at the plugin-root level" branch — when files
        // sit directly at the configured pluginDir without a per-package
        // subdir wrapper. The host gets a context for the root path
        // itself; the DLL fails to load because it's not a real assembly,
        // but TryLoadPlugin swallows that and returns the registered
        // BowirePluginHost.
        var temp = Path.Combine(Path.GetTempPath(),
            "bowire-plugin-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        // Bytes that look like a PE header but aren't a real assembly —
        // load fails, host catches, contract holds.
        File.WriteAllBytes(Path.Combine(temp, "Fake.dll"), new byte[] { 0x4D, 0x5A, 0x00, 0x00 });
        try
        {
            var services = new ServiceCollection();
            services.AddBowirePlugins(temp);

            Assert.Contains(services, d => d.ServiceType == typeof(BowirePluginHost));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void AddBowirePlugins_IConfiguration_With_Bound_PluginDir_Registers_Host()
    {
        var temp = Path.Combine(Path.GetTempPath(),
            "bowire-plugin-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "package-x"));
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Bowire:PluginDir"] = temp,
                })
                .Build();
            var services = new ServiceCollection();

            services.AddBowirePlugins(config);

            Assert.Contains(services, d => d.ServiceType == typeof(BowirePluginHost));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
