// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Reflection;

namespace Kuestenlogik.Bowire.Protocol.Rest;

/// <summary>
/// Runtime lookup for the registered <see cref="IBowireOpenApiAdapter"/>.
///
/// <para>
/// Adapter packages (e.g. <c>Kuestenlogik.Bowire.Protocol.Rest.OpenApi3</c>)
/// register themselves at module load via <see cref="Register"/>;
/// when none has done so, <see cref="TryGet"/> walks the loaded
/// AppDomain assemblies looking for a public, non-abstract
/// implementation of <see cref="IBowireOpenApiAdapter"/> and caches
/// the first one it finds. Both paths support the same standalone /
/// embedded shape — standalone Tool transitively pulls
/// <c>Bowire.Protocol.Rest.OpenApi3</c> so the discovery path resolves
/// automatically; embedded hosts can wire their own adapter explicitly
/// via <see cref="Register"/> when they need to pin a different
/// <c>Microsoft.OpenApi</c> line.
/// </para>
/// </summary>
public static class BowireOpenApiAdapterRegistry
{
    private static IBowireOpenApiAdapter? _explicit;
    private static IBowireOpenApiAdapter? _scanCache;
    private static readonly object _gate = new();

    /// <summary>
    /// Register an adapter explicitly. Subsequent <see cref="TryGet"/>
    /// calls return this instance, bypassing the AppDomain scan.
    /// Useful for embedded hosts that wire a non-default OpenAPI line
    /// (e.g. an OpenApi2 adapter when ASP.NET's <c>AddOpenApi()</c> is
    /// also active in the same process).
    /// </summary>
    public static void Register(IBowireOpenApiAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        lock (_gate)
        {
            _explicit = adapter;
        }
    }

    /// <summary>
    /// Returns the registered adapter, or <c>null</c> when none could
    /// be resolved. The REST plugin treats a null adapter as "no
    /// OpenAPI discovery available" — the workbench's form-based
    /// invocation surface still works, but URL discovery and
    /// mock-from-OpenAPI become no-ops.
    /// </summary>
    public static IBowireOpenApiAdapter? TryGet()
    {
        if (_explicit is { } registered) return registered;
        if (_scanCache is { } cached) return cached;
        lock (_gate)
        {
            if (_explicit is { } e) return e;
            if (_scanCache is { } c) return c;
            _scanCache = ScanLoadedAssemblies();
            return _scanCache;
        }
    }

    private static IBowireOpenApiAdapter? ScanLoadedAssemblies()
    {
        // Walk the loaded AppDomain looking for public, non-abstract
        // implementers with a parameterless ctor. Adapter packages
        // are expected to expose exactly one impl, mirroring the
        // protocol plugin discovery shape — in the normal NuGet
        // single-version scenario there's only ever one candidate.
        var candidates = new List<IBowireOpenApiAdapter>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = Array.FindAll(ex.Types, t => t is not null)!; }
            catch { continue; }
            foreach (var t in types)
            {
                if (!typeof(IBowireOpenApiAdapter).IsAssignableFrom(t)) continue;
                if (!t.IsClass || t.IsAbstract) continue;
                if (t.GetConstructor(Type.EmptyTypes) is null) continue;
                try { candidates.Add((IBowireOpenApiAdapter)Activator.CreateInstance(t)!); }
                catch { /* skip — try the next candidate */ }
            }
        }
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        // Two+ adapters loaded — edge case (a sideloaded plugin pulled
        // a second one). Pick the adapter whose declared
        // OpenApiLibraryMajorVersion matches the Microsoft.OpenApi
        // assembly already in the process, otherwise fall back to the
        // lowest-numbered version so the choice is deterministic.
        var loadedMajor = TryReadLoadedOpenApiMajor();
        if (loadedMajor is { } major)
        {
            var match = candidates.Find(a => a.OpenApiLibraryMajorVersion == major);
            if (match is not null) return match;
        }
        candidates.Sort(static (a, b) => a.OpenApiLibraryMajorVersion.CompareTo(b.OpenApiLibraryMajorVersion));
        return candidates[0];
    }

    /// <summary>
    /// Read the major version of the <c>Microsoft.OpenApi</c>
    /// assembly currently loaded in the process, or null when none is
    /// loaded yet (cold-start before any adapter has parsed a doc).
    /// </summary>
    private static int? TryReadLoadedOpenApiMajor()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(asm => asm.GetName())
            .FirstOrDefault(name => string.Equals(name.Name, "Microsoft.OpenApi", StringComparison.OrdinalIgnoreCase));
        return loaded?.Version?.Major;
    }

    /// <summary>Test helper — clears both the explicit registration and the scan cache.</summary>
    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _explicit = null;
            _scanCache = null;
        }
    }
}
