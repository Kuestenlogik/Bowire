// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.Loader;

namespace Kuestenlogik.Bowire.PluginLoading;

/// <summary>
/// Per-plugin <see cref="AssemblyLoadContext"/> that isolates plugin-private
/// dependencies from the host and from other plugins. Plugins can carry
/// their own versions of third-party libraries without clashing with
/// another plugin's copy; shared contract assemblies — <c>Kuestenlogik.Bowire*</c>,
/// the BCL, and the ASP.NET framework — are delegated to the default ALC
/// so <see cref="IBowireProtocol"/> and friends keep a single identity
/// across every context.
/// </summary>
/// <remarks>
/// <para>
/// Resolution rules inside the plugin's own <c>Load</c> hook:
/// </para>
/// <list type="number">
///   <item>Assembly name matches a shared prefix → return <c>null</c>,
///   which tells the runtime to fall back to the default ALC. That's
///   how the plugin gets the host's copy of <see cref="IBowireProtocol"/>
///   instead of a plugin-local type that wouldn't be assignable.</item>
///   <item>Otherwise, look for <c>&lt;AssemblyName&gt;.dll</c> in the
///   plugin's directory and load it through this context.</item>
///   <item>Nothing found → <c>null</c>, runtime raises
///   <c>FileNotFoundException</c> at the call site (same contract as
///   an unresolved reference in any ALC).</item>
/// </list>
/// <para>
/// Plugins downloaded via <c>bowire plugin install</c> land in
/// <c>&lt;pluginDir&gt;/&lt;packageId&gt;/</c> as a flat folder of DLLs
/// (no <c>.deps.json</c>), so we look for deps by filename in that
/// folder rather than going through <see cref="AssemblyDependencyResolver"/>.
/// </para>
/// <para>
/// <see cref="AssemblyLoadContext.IsCollectible"/> is <c>true</c> so
/// embedded hosts can unload / hot-reload a plugin in-process via
/// <see cref="BowirePluginHost"/>. Unload only actually reclaims memory
/// once every reference held by the host drops — hold plugin types
/// through <c>WeakReference</c> or scope them to a DI scope that
/// disposes with the plugin.
/// </para>
/// </remarks>
public sealed class BowirePluginLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Assembly-name prefixes that always resolve from the default ALC
    /// so every plugin shares the host's copy of the matching DLL.
    /// Covers the Bowire contract assembly, BCL types, ASP.NET, and
    /// the .NET Standard facade.
    /// </summary>
    public static IReadOnlyList<string> DefaultSharedPrefixes { get; } = new[]
    {
        "Kuestenlogik.Bowire",
        "System.",
        "System,",
        "Microsoft.",
        "NETStandard.",
        "netstandard"
    };

    private readonly string _pluginDir;
    private readonly IReadOnlyList<string> _sharedPrefixes;
    private readonly AssemblyDependencyResolver? _resolver;

    /// <summary>
    /// Build a context for the plugin whose DLLs live under
    /// <paramref name="pluginDir"/>.
    /// </summary>
    /// <param name="pluginDir">Absolute path to the plugin's folder.</param>
    /// <param name="additionalSharedPrefixes">
    /// Extra assembly-name prefixes that should also delegate to the
    /// default ALC. Embedded hosts use this to mark their own SDK
    /// assemblies as shared.
    /// </param>
    public BowirePluginLoadContext(string pluginDir, IEnumerable<string>? additionalSharedPrefixes = null)
        : base(name: DeriveName(pluginDir), isCollectible: true)
    {
        // Null/empty check already ran inside DeriveName (needed because
        // the base ctor has to be called first) — do it again so the
        // field assignment path is obviously safe.
        ArgumentException.ThrowIfNullOrEmpty(pluginDir);
        _pluginDir = Path.GetFullPath(pluginDir);
        _sharedPrefixes = additionalSharedPrefixes is null
            ? DefaultSharedPrefixes
            : DefaultSharedPrefixes.Concat(additionalSharedPrefixes).ToList();

        // Best-effort AssemblyDependencyResolver — the .NET-recommended
        // path for plugin ALCs. Reads `<manifest>.deps.json` next to
        // the plugin's main DLL and uses it to find both managed
        // dependencies and native libraries with correct RID-specific
        // resolution. Falls back to plain filename lookup in `Load`
        // below when no .deps.json is present (the legacy `bowire
        // plugin install` from a flat-folder nupkg ships managed DLLs
        // without `.deps.json` — the resolver-less path still works
        // for that shape).
        var packageId = Path.GetFileName(_pluginDir.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var manifestPath = Path.Combine(_pluginDir, packageId + ".dll");
        if (File.Exists(manifestPath))
        {
            try { _resolver = new AssemblyDependencyResolver(manifestPath); }
            catch { /* no .deps.json or invalid layout — stay on the filename-lookup path */ }
        }
    }

    private static string DeriveName(string pluginDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginDir);
        var trimmed = pluginDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return "BowirePlugin:" + Path.GetFileName(trimmed);
    }

    /// <summary>Absolute directory the context resolves plugin-private deps from.</summary>
    public string PluginDirectory => _pluginDir;

    /// <summary>
    /// True when <paramref name="assemblyName"/> should resolve from the
    /// default ALC rather than the plugin directory. Exposed so callers
    /// (and tests) can reason about the sharing decision without loading
    /// an assembly.
    /// </summary>
    public bool IsShared(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName)) return false;
        foreach (var prefix in _sharedPrefixes)
        {
            if (assemblyName.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsShared(assemblyName.Name))
        {
            // Delegate to default ALC — returning null lets the runtime
            // fall back. Doing this for the shared contract assemblies
            // is what preserves type identity of IBowireProtocol &
            // friends across the ALC boundary.
            return null;
        }

        // Plugin-private. Resolver path first — when the plugin shipped
        // with a `.deps.json` we honour what the build system declared
        // (correct RID-specific assemblies, NuGet RID-graph fall-through,
        // etc.). Falls back to plain filename lookup so flat-folder
        // installs without `.deps.json` still load — that's how
        // `bowire plugin install` lays packages out today.
        if (_resolver is not null)
        {
            var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
            if (resolved is not null) return LoadFromAssemblyPath(resolved);
        }

        if (string.IsNullOrEmpty(assemblyName.Name)) return null;
        var candidate = Path.Combine(_pluginDir, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }

    /// <inheritdoc />
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        // Resolver path is the only correct answer for native libs —
        // it knows about RID-specific (`runtimes/win-x64/native/…`)
        // layouts that a naive filename lookup wouldn't find. When
        // the plugin shipped without a `.deps.json`, fall back to the
        // legacy filename-in-pluginDir lookup so existing flat-folder
        // native installs keep working.
        if (_resolver is not null)
        {
            var resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (resolved is not null) return LoadUnmanagedDllFromPath(resolved);
        }

        var candidate = Path.Combine(_pluginDir, unmanagedDllName);
        if (File.Exists(candidate)) return LoadUnmanagedDllFromPath(candidate);

        // Try the DLL extension for platform parity on Windows users
        // who forget the extension.
        var withExt = Path.Combine(_pluginDir, unmanagedDllName + ".dll");
        if (File.Exists(withExt)) return LoadUnmanagedDllFromPath(withExt);

        return IntPtr.Zero;
    }
}
