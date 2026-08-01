// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Plugins;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// Loaders for tests that need to hand one to a CLI entry point but do
/// not care about plugin loading itself (#546).
/// </summary>
/// <remarks>
/// Before the loader became an object these call sites passed
/// <c>plugins: TestPluginLoaders.None()</c>, which fell through the precedence chain to
/// <c>BOWIRE_PLUGIN_DIR</c> — the directory <c>TestPluginIsolation</c>
/// redirects at module load. Naming the directory outright says the same
/// thing without routing it through a process-global variable, which is
/// the point of the ticket.
/// </remarks>
internal static class TestPluginLoaders
{
    /// <summary>
    /// A loader over a directory that does not exist, so
    /// <see cref="BowirePluginLoader.Load"/> is a no-op and no load
    /// context is ever created.
    /// </summary>
    public static BowirePluginLoader None()
        => For(Path.Combine(Path.GetTempPath(), "bowire-no-plugins-" + Guid.NewGuid().ToString("N")));

    /// <summary>A loader over an explicit directory.</summary>
    public static BowirePluginLoader For(string directory)
        => new(new BowirePluginOptions { PluginDirectory = directory });
}
