// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Kuestenlogik.Bowire.PluginLoading;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Exercises the collectible-ALC + hot-reload contract: load a plugin,
/// unload it, verify the ALC gets collected once references drop, and
/// verify Reload swaps cleanly without leaking the previous context.
/// </summary>
public sealed class BowirePluginHostTests : IDisposable
{
    private readonly string _tempDir;

    public BowirePluginHostTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_CreatesContextUnderPackageIdKey()
    {
        var pluginDir = CreatePluginDir("Sample.Plugin");

        var host = new BowirePluginHost();
        var ctx = host.Load(pluginDir);

        Assert.True(host.LoadedPlugins.ContainsKey("Sample.Plugin"));
        Assert.Same(ctx, host.LoadedPlugins["Sample.Plugin"]);
        Assert.True(ctx.IsCollectible, "Plugin ALCs must be collectible to enable hot-reload.");
    }

    [Fact]
    public void Load_SameDirTwice_ReplacesExistingContext()
    {
        var pluginDir = CreatePluginDir("ReloadMe");

        var host = new BowirePluginHost();
        var first = host.Load(pluginDir);
        var second = host.Load(pluginDir);

        Assert.NotSame(first, second);
        Assert.Same(second, host.LoadedPlugins["ReloadMe"]);
    }

    [Fact]
    public void Unload_ReturnsFalse_WhenPluginWasNeverLoaded()
    {
        var host = new BowirePluginHost();
        Assert.False(host.Unload("never-loaded"));
    }

    [Fact]
    public void Unload_RemovesFromLoadedPluginsImmediately()
    {
        var pluginDir = CreatePluginDir("Drop");
        var host = new BowirePluginHost();
        host.Load(pluginDir);

        Assert.True(host.Unload("Drop"));
        Assert.False(host.LoadedPlugins.ContainsKey("Drop"));
    }

    [Fact]
    public void Unload_GCCollectsContext_AfterReferencesDrop()
    {
        // Scope the plugin load so no local references survive into
        // the GC loop — the ALC should then be fully collectible.
        var host = new BowirePluginHost();
        var pluginDir = CreatePluginDir("Collect");

        LoadAndDrop(host, pluginDir);

        Assert.True(host.Unload("Collect"));

        // Collectible ALCs unload over several GC cycles in the CLR.
        // 10 cycles with a waitForPendingFinalizers is the idiomatic
        // loop for unload-verification tests in the .NET docs.
        for (var i = 0; i < 10 && !host.IsUnloaded("Collect"); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.True(host.IsUnloaded("Collect"),
            "Plugin ALC should have unloaded after dropping references; " +
            "if this fails the host is accidentally pinning the context.");
    }

    // NoInlining + scope isolation so the JIT can't extend the lifetime
    // of the returned context reference into the caller's frame and
    // accidentally keep the ALC alive during the GC loop above.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadAndDrop(BowirePluginHost host, string pluginDir)
    {
        _ = host.Load(pluginDir);
    }

    [Fact]
    public void Reload_ReplacesContext_ForTheSamePackageId()
    {
        var pluginDir = CreatePluginDir("Swap");
        var host = new BowirePluginHost();
        var first = host.Load(pluginDir);

        var second = host.Reload(pluginDir);

        Assert.NotSame(first, second);
        Assert.Same(second, host.LoadedPlugins["Swap"]);
    }

    // Writes a throwaway plugin directory with one DLL in it so
    // BowirePluginHost.Load has something to chew on. We copy
    // xunit.core since it's always on disk at test time and is neither
    // in Kuestenlogik.Bowire's nor the host's shared-prefix list.
    private string CreatePluginDir(string packageId)
    {
        var dir = Path.Combine(_tempDir, packageId);
        Directory.CreateDirectory(dir);

        var source = typeof(FactAttribute).Assembly.Location;
        var target = Path.Combine(dir, Path.GetFileName(source));
        File.Copy(source, target, overwrite: true);

        return dir;
    }
}
