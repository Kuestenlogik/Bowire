// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Keeps the whole test assembly away from the developer's real plugin
/// directory (#543).
/// </summary>
/// <remarks>
/// <para>
/// Anything that reaches <c>PluginManager.LoadPlugins</c> without an
/// explicit directory — <c>MockCommand.RunAsync</c> among others —
/// resolves <c>BOWIRE_PLUGIN_DIR</c> and then <c>~/.bowire/plugins</c>.
/// On a developer machine that directory holds whatever they last
/// installed with <c>bowire plugin install</c>, so tests silently take a
/// dependency on state outside the repository. It bit
/// <c>MockCommandAutoInstallTests</c>: with
/// <c>Kuestenlogik.Bowire.Protocol.Dis</c> installed, "dis" counted as
/// present, only one of the two protocols was missing, and the test
/// failed with 1 instead of 2 — while CI, which has no plugin directory,
/// stayed green.
/// </para>
/// <para>
/// This has to be an assembly-wide module initializer rather than a
/// per-class fixture, because <c>PluginManager</c> keeps its loaded
/// contexts in static fields (<c>s_pluginContexts</c> /
/// <c>s_loadedSubdirs</c>). Assembly loading is process-global — an ALC
/// cannot be scoped to a test — so once ANY test has loaded the real
/// plugins, every later test sees them through
/// <c>AppDomain.CurrentDomain.GetAssemblies()</c> no matter what it sets
/// afterwards. The only reliable moment is before the first test runs.
/// </para>
/// <para>
/// An explicitly set <c>BOWIRE_PLUGIN_DIR</c> is left alone so CI or a
/// developer can still point the suite somewhere deliberately. Tests that
/// pass an explicit directory to <c>LoadPlugins</c> are unaffected either
/// way.
/// </para>
/// </remarks>
internal static class TestPluginIsolation
{
    [ModuleInitializer]
    internal static void RedirectPluginDirectory()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BOWIRE_PLUGIN_DIR")))
        {
            return;
        }

        // Process-scoped so parallel test hosts (and a leftover directory
        // from a killed run) cannot see each other's plugins.
        var isolated = Path.Combine(
            Path.GetTempPath(),
            $"bowire-test-plugins-{Environment.ProcessId}");
        Directory.CreateDirectory(isolated);
        Environment.SetEnvironmentVariable("BOWIRE_PLUGIN_DIR", isolated);
    }
}
