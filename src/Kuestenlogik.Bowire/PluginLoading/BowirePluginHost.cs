// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace Kuestenlogik.Bowire.PluginLoading;

/// <summary>
/// Lifecycle manager for Bowire plugin <see cref="AssemblyLoadContext"/>s.
/// Tracks one <see cref="BowirePluginLoadContext"/> per plugin directory
/// (keyed by the directory's leaf name, which is the package id), and
/// supports <see cref="Unload"/> + <see cref="Reload"/> so embedded hosts
/// can swap a plugin in-process after <c>bowire plugin update</c> has
/// rewritten its files.
/// </summary>
/// <remarks>
/// <para>
/// Unload only releases memory once the host drops every reference to
/// plugin types. If you cache plugin instances directly the ALC lingers
/// indefinitely. Two mitigations:
/// </para>
/// <list type="bullet">
///   <item>Resolve plugin services from a DI scope that you dispose
///   alongside the plugin — scope-disposal drops every cached
///   transient/scoped instance.</item>
///   <item>Hold plugin instances through <see cref="WeakReference"/>
///   where you can, so the GC can collect them once usage ends.</item>
/// </list>
/// <para>
/// The host itself keeps only a <see cref="WeakReference"/> to every
/// ALC after an unload call, so it never prevents collection. Whether
/// the unload actually completed is observable via
/// <see cref="IsUnloaded"/>.
/// </para>
/// </remarks>
public sealed class BowirePluginHost
{
    private readonly ConcurrentDictionary<string, BowirePluginLoadContext> _contexts =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, WeakReference> _unloadingContexts =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<string>? _additionalSharedPrefixes;

    /// <summary>Build a host. Optional additional shared prefixes propagate to every plugin context.</summary>
    public BowirePluginHost(IEnumerable<string>? additionalSharedPrefixes = null)
    {
        _additionalSharedPrefixes = additionalSharedPrefixes?.ToList();
    }

    /// <summary>Currently loaded plugin directories, keyed by package id (dir-leaf name).</summary>
    public IReadOnlyDictionary<string, BowirePluginLoadContext> LoadedPlugins => _contexts;

    /// <summary>
    /// Load every DLL in <paramref name="pluginDir"/> into a fresh
    /// <see cref="BowirePluginLoadContext"/>. If this plugin was loaded
    /// before, the previous context is unloaded first — use
    /// <see cref="Reload"/> for the intended sequence so callers can
    /// distinguish a fresh install from a hot-replace.
    /// </summary>
    public BowirePluginLoadContext Load(string pluginDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginDir);
        var key = Path.GetFileName(Path.GetFullPath(pluginDir.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        ArgumentException.ThrowIfNullOrEmpty(key, nameof(pluginDir));

        // If already loaded, drop the existing context first — this
        // keeps Load idempotent but callers should prefer Reload for
        // the explicit swap semantics.
        if (_contexts.TryRemove(key, out var existing))
        {
            BeginUnload(key, existing);
        }

        var ctx = new BowirePluginLoadContext(pluginDir, _additionalSharedPrefixes);

        // Load ONLY the manifest assembly (named after the package id,
        // matching the install layout `<pluginDir>/<packageId>/<packageId>.dll`).
        // The ALC's Load callback resolves every transitive reference on
        // demand — shared-prefix names delegate to the default ALC so
        // contract types (IBowireProtocol, BowireServiceInfo, …) keep a
        // single identity across the host ↔ plugin boundary; everything
        // else falls back to a filename lookup inside the plugin folder.
        //
        // See PluginManager.LoadPlugins for the long-form explanation —
        // the same dual-load bug existed here and the fix is the same:
        // never call LoadFromAssemblyPath on a copy of Kuestenlogik.Bowire
        // that ships next to the plugin, because that creates a second
        // identity of every contract type and breaks
        // BowireProtocolRegistry.Discover's `IsAssignableFrom` check.
        var manifest = Path.Combine(pluginDir, key + ".dll");
        if (File.Exists(manifest))
        {
            try { ctx.LoadFromAssemblyPath(Path.GetFullPath(manifest)); }
            catch { /* skip — plugin appears empty in discovery */ }
        }
        _contexts[key] = ctx;
        return ctx;
    }

    /// <summary>
    /// Unload the plugin with the given <paramref name="packageId"/>.
    /// The underlying context is marked collectible and begins its
    /// teardown once every type reference the host holds drops.
    /// Returns <c>false</c> when no matching plugin is loaded.
    /// </summary>
    public bool Unload(string packageId)
    {
        if (!_contexts.TryRemove(packageId, out var ctx)) return false;
        BeginUnload(packageId, ctx);
        return true;
    }

    /// <summary>
    /// Hot-replace a plugin: unload the old context and load the
    /// updated one from <paramref name="pluginDir"/>. Combines
    /// <see cref="Unload"/> + <see cref="Load"/> with a single
    /// operation-level name for callers who want the ordering to be
    /// unambiguous.
    /// </summary>
    public BowirePluginLoadContext Reload(string pluginDir)
    {
        return Load(pluginDir);
    }

    /// <summary>
    /// <c>true</c> when <paramref name="packageId"/> was previously
    /// loaded, an unload was issued, <i>and</i> the GC has actually
    /// finalised the context. Useful in tests to prove the host isn't
    /// leaking references; in production the answer is usually "not
    /// yet" because the host still holds types through DI.
    /// </summary>
    public bool IsUnloaded(string packageId)
    {
        if (!_unloadingContexts.TryGetValue(packageId, out var weak)) return false;
        return !weak.IsAlive;
    }

    private void BeginUnload(string packageId, BowirePluginLoadContext ctx)
    {
        // Ask the ALC to start unloading. Actual collection happens
        // when every reference has been released — the caller needs
        // to drop its own service instances / types / DI caches.
        ctx.Unload();
        _unloadingContexts[packageId] = new WeakReference(ctx);
    }
}
