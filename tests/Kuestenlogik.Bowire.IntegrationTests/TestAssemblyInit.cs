// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Force-loads every <c>Kuestenlogik.Bowire*.dll</c> sitting in the test
/// output directory into the AppDomain before any test runs.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Kuestenlogik.Bowire.BowireProtocolRegistry"/> discovers
/// plugins by scanning the <em>already-loaded</em> assemblies, and the
/// endpoint layer caches the first registry it builds in a process-wide
/// static (<c>BowireEndpointHelpers._registry</c>). In production that's
/// fine — one app, one registry. In the test process many
/// <c>MapBowire</c> hosts run in parallel, so whichever one builds the
/// registry first wins the cache; if its app happened to run before the
/// gRPC / SignalR plugin assemblies were loaded, the cached registry is
/// missing them and <c>ProtocolsEndpoint_ReturnsGrpcAndSignalR</c> (and
/// friends) flake.
/// </para>
/// <para>
/// Loading every plugin assembly here, at module-init time (before the
/// first test and before any host starts), makes the discovery result
/// order-independent: no matter which app caches the registry first, the
/// full plugin set is already in the AppDomain.
/// </para>
/// </remarks>
internal static class TestAssemblyInit
{
    [ModuleInitializer]
    public static void LoadAllBowirePluginAssemblies()
    {
        string? baseDir;
        try { baseDir = AppContext.BaseDirectory; }
        catch { return; }
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) return;

        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name is { } n) loaded.Add(n);
        }

        foreach (var dll in Directory.EnumerateFiles(baseDir, "Kuestenlogik.Bowire*.dll"))
        {
            var simpleName = Path.GetFileNameWithoutExtension(dll);
            if (loaded.Contains(simpleName)) continue;
            try { Assembly.LoadFrom(dll); }
            catch { /* a non-loadable sibling dll shouldn't abort the sweep */ }
        }
    }
}
