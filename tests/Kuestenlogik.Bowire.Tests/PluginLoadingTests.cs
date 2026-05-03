// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Guards the <c>AddBowirePlugins</c> contract on
/// <see cref="BowireServiceCollectionExtensions"/>: embedded hosts use
/// it to point the workbench at an out-of-tree plugin directory before
/// calling <c>AddBowire()</c>.
/// </summary>
public sealed class PluginLoadingTests
{
    [Fact]
    public void AddBowirePlugins_NonExistentPath_DoesNotThrow()
    {
        // Callers wire the plugin dir from config unconditionally — a
        // still-empty install directory must not crash startup.
        var services = new ServiceCollection();
        var result = services.AddBowirePlugins(
            Path.Combine(Path.GetTempPath(), "bowire-definitely-not-here-" + Guid.NewGuid().ToString("N")));
        Assert.Same(services, result);
    }

    [Fact]
    public void AddBowirePlugins_EmptyPath_IsNoOp()
    {
        var services = new ServiceCollection();
        var result = services.AddBowirePlugins("");
        Assert.Same(services, result);
    }

    [Fact]
    public void AddBowirePlugins_NullPath_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => services.AddBowirePlugins((string)null!));
    }

    [Fact]
    public void AddBowirePlugins_EmptyDirectory_IsNoOp()
    {
        var temp = Path.Combine(Path.GetTempPath(), "bowire-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var services = new ServiceCollection();
            var result = services.AddBowirePlugins(temp);
            Assert.Same(services, result);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
