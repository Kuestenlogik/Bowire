// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.PluginLoading;

namespace Kuestenlogik.Bowire.App.Plugins;

/// <summary>
/// The single owner of one Bowire instance's plugin load contexts (#546).
/// </summary>
/// <remarks>
/// <para>
/// Replaces three static fields that used to live on
/// <see cref="PluginManager"/>: the context list, the "already loaded"
/// subdirectory set, and the last result snapshot. The set was a duplicate
/// ledger — it recorded what <c>_contexts</c> already knew, one key shape
/// removed, and the two could drift. Here they are the same dictionary:
/// <c>_contexts.ContainsKey(packageId)</c> <i>is</i> the already-loaded
/// check, so there is nothing to keep in sync.
/// </para>
/// <para>
/// Keyed by package id rather than by absolute path. Within one plugin
/// directory the two are equivalent — the package id is the subdirectory's
/// name — and the case the path key existed for, two Bowire instances in
/// one process fighting over one global set, is exactly what having an
/// instance removes.
/// </para>
/// </remarks>
internal sealed class BowirePluginLoader : IBowirePluginLoader
{
    // One entry per loaded package. This dictionary is both the context
    // registry and the load ledger — see the type remarks.
    private readonly Dictionary<string, BowirePluginLoadContext> _contexts =
        new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<PluginLoadResult> _last = Array.Empty<PluginLoadResult>();

    public BowirePluginLoader(BowirePluginOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    /// <summary>Convenience ctor for callers that only have a directory.</summary>
    public BowirePluginLoader(string pluginDirectory)
        : this(BowirePluginOptions.Resolve(pluginDirectory))
    {
    }

    public BowirePluginOptions Options { get; }

    public IReadOnlyList<PluginLoadResult> LastResults => _last;

    /// <summary>
    /// Load all plugin assemblies from <see cref="Options"/>. Each package
    /// subdirectory gets its own <see cref="BowirePluginLoadContext"/> so
    /// plugin-private dependencies don't collide; shared contract
    /// assemblies (<c>Kuestenlogik.Bowire*</c>, <c>System.*</c>,
    /// <c>Microsoft.*</c>) delegate to the default ALC so the host's
    /// interface types keep one identity across every context.
    /// </summary>
    public IReadOnlyList<PluginLoadResult> Load()
    {
        // Both tiers, user first (#28 Phase D). BowirePluginRoot applies the
        // overlay rule, so a package installed locally over a machine-wide
        // one is loaded once, from the user's copy — which is the point of an
        // overlay: trying a newer build without an administrator changing
        // what everybody else gets.
        //
        // The precedence lands for free on the duplicate check below: the
        // machine-tier twin arrives second and is skipped as AlreadyLoaded.
        var results = new List<PluginLoadResult>();
        foreach (var (subDir, _, _) in Kuestenlogik.Bowire.Plugins.BowirePluginRoot.EnumeratePackagesUnder(
                     Options.PluginDirectory, includeMachineTier: !Options.IsExplicit))
        {
            var normalised = Path.GetFullPath(subDir);
            var packageId = Path.GetFileName(normalised);

            // Idempotent re-entry: Program.cs and BrowserUiHost both load
            // as a defence-in-depth measure, and `bowire mock` loads again
            // after an auto-install. Without this skip every repeat would
            // spin up a fresh context for the same package, so
            // AppDomain.CurrentDomain.GetAssemblies() ends up holding N
            // copies of the plugin assembly and BowireProtocolRegistry
            // registers each protocol N times — visible to users as
            // duplicate entries in the sidebar.
            if (_contexts.ContainsKey(packageId))
            {
                results.Add(new PluginLoadResult(packageId, normalised,
                    PluginLoadStatus.AlreadyLoaded, null));
                continue;
            }

            var manifest = Path.Combine(subDir, packageId + ".dll");
            if (!File.Exists(manifest))
            {
                results.Add(new PluginLoadResult(packageId, normalised,
                    PluginLoadStatus.ManifestMissing,
                    $"Expected manifest assembly '{packageId}.dll' not found in {subDir}."));
                continue;
            }

            // Pre-load contract version check. Read the manifest's
            // referenced Kuestenlogik.Bowire version from metadata only —
            // no actual assembly load — and reject on a major mismatch.
            // Catches the common "tool was updated, plugin still pinned to
            // the old Bowire contract" failure: without this the load
            // succeeds and the host throws a TypeLoadException deep inside
            // BowireProtocolRegistry.Discover instead.
            var hostVersion = Options.HostContractVersion ?? PluginManifestProbe.HostBowireVersion;
            var pluginRefVersion = PluginManifestProbe.ReadReferencedBowireVersion(manifest);
            if (!PluginManifestProbe.IsContractCompatible(pluginRefVersion, hostVersion))
            {
                results.Add(new PluginLoadResult(packageId, normalised,
                    PluginLoadStatus.ContractMajorMismatch,
                    $"Plugin references Kuestenlogik.Bowire {pluginRefVersion} but host is " +
                    $"{hostVersion}. Run `bowire plugin update " +
                    $"{packageId}` to pull a build compiled against the current host."));
                continue;
            }

            BowirePluginLoadContext ctx;
            // ALC ctor surface: any IO/argument failure on the plugin
            // directory. One bad context must not block the rest.
            try { ctx = new BowirePluginLoadContext(subDir); }
            catch (Exception ex)
            {
                results.Add(new PluginLoadResult(packageId, normalised,
                    PluginLoadStatus.AssemblyLoadFailed,
                    $"Could not create plugin ALC for {packageId}: {ex.Message}"));
                continue;
            }

            // Record before the load attempt, not after. The context
            // exists either way, so a retry would create a second one for
            // the same package — which is the duplicate the ledger is for.
            _contexts[packageId] = ctx;

            // Load ONLY the manifest assembly. The runtime then walks its
            // metadata table and asks ctx.Load() for each reference on
            // demand; ctx.Load() delegates shared-prefix names to the
            // default ALC so plugin and host share one IBowireProtocol
            // identity, and resolves everything else from the plugin
            // folder (AssemblyDependencyResolver when a .deps.json is
            // present, filename lookup otherwise).
            //
            // The catch stays here rather than delegating to
            // BowirePluginHost.Load, which swallows the exception — the
            // message below is what tells an operator which DLL is broken.
            try
            {
                ctx.LoadFromAssemblyPath(Path.GetFullPath(manifest));
                results.Add(new PluginLoadResult(packageId, normalised,
                    PluginLoadStatus.Loaded, null));
            }
            catch (Exception ex)
            {
                results.Add(new PluginLoadResult(packageId, normalised,
                    PluginLoadStatus.AssemblyLoadFailed,
                    $"LoadFromAssemblyPath failed for {manifest}: {ex.Message}"));
            }
        }

        _last = results.AsReadOnly();
        PluginLoadResultStore.Publish(_last);
        return _last;
    }

    /// <summary>
    /// Instantiate every <typeparamref name="T"/> contributed by a plugin
    /// this loader owns, or by a <c>Kuestenlogik.Bowire*</c> assembly
    /// shipped next to the host.
    /// </summary>
    /// <remarks>
    /// Exceptions during type scan or instantiation are reported to stderr
    /// and swallowed, so a single broken plugin doesn't take down the
    /// host. Returns an empty list when nothing contributes.
    /// </remarks>
    public List<T> EnumerateServices<T>() where T : class
    {
        var results = new List<T>();
        var contract = typeof(T);
        // Global type-identity guard: a NuGet-installed plugin and the
        // Tool's compiled-in copy of the same assembly would otherwise
        // both contribute an instance (two MQTT transport hosts fighting
        // over one broker port). Plugin-directory installs win — they are
        // scanned first.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void ScanAssembly(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }
            foreach (var type in types)
            {
                if (type is null) continue;
                if (type.IsAbstract || type.IsInterface) continue;
                if (!contract.IsAssignableFrom(type)) continue;
                if (type.FullName is { } fullName && !seen.Add(fullName)) continue;
                // Plugin instantiation: parameterless ctor of a 3rd-party
                // type — anything can come out of its static initialisers.
                try
                {
                    if (Activator.CreateInstance(type) is T instance)
                    {
                        results.Add(instance);
                    }
                }
                catch (Exception ex)
                {
                    // Runtime plugin-discovery diagnostic — no CLI IO
                    // context to thread through, so it stays on the
                    // process-global stderr rather than the PluginIo-routed
                    // CLI surface.
                    Console.Error.WriteLine(
                        $"  warning: failed to instantiate plugin type '{type.FullName}': {ex.Message}");
                }
            }
        }

        foreach (var ctx in _contexts.Values)
        {
            foreach (var asm in ctx.Assemblies)
            {
                ScanAssembly(asm);
            }
        }

        // The Tool ships the first-party protocol plugins compiled in
        // (Bundle.Workbench references), so their mock contributions —
        // GrpcMockHostingExtension, MqttMockTransportHost,
        // RestMockHostingExtension, … — live in the default load context,
        // not in a plugin ALC. Without this pass `bowire mock` silently ran
        // with zero hosting extensions / transport hosts unless the same
        // plugin was ALSO installed into the plugin directory (#511: no
        // MQTT broker, no gRPC reflection, no OpenAPI re-serve on
        // recordings carrying those steps).
        foreach (var asm in EnumerateBuiltInBowireAssemblies())
        {
            ScanAssembly(asm);
        }

        return results;
    }

    /// <summary>
    /// Load + return every <c>Kuestenlogik.Bowire*</c> assembly sitting
    /// next to the entry assembly. Same on-disk sweep the protocol
    /// registry uses — contributions must be findable even when nothing
    /// has touched the assembly's types yet, because lazy loading makes
    /// "already in the AppDomain" a race with whoever asked first.
    /// </summary>
    /// <remarks>
    /// Process-global by construction: <c>Assembly.Load</c> targets the
    /// default ALC, so two loaders see the same built-in contributions.
    /// That is correct — they are the host's own assemblies, not any
    /// instance's plugins — and it cannot be scoped.
    /// </remarks>
    private static List<Assembly> EnumerateBuiltInBowireAssemblies()
    {
        var results = new List<Assembly>();
        string[] files;
        try
        {
            files = Directory.GetFiles(AppContext.BaseDirectory, "Kuestenlogik.Bowire*.dll");
        }
        catch (IOException) { return results; }
        catch (UnauthorizedAccessException) { return results; }

        foreach (var file in files)
        {
            try
            {
                var name = AssemblyName.GetAssemblyName(file);
                results.Add(Assembly.Load(name));
            }
            catch (BadImageFormatException) { /* native / non-.NET satellite — skip */ }
            catch (FileLoadException) { /* version clash — the loaded copy is fine, but unreachable here — skip */ }
            catch (IOException) { /* unreadable file — skip */ }
        }
        return results;
    }
}
