// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.App.Plugins;
using Kuestenlogik.Bowire.PluginLoading;
using NuGetNull = NuGet.Common.NullLogger;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Manages Bowire protocol plugins installed from NuGet packages.
/// Plugins are stored in ~/.bowire/plugins/{packageId}/ and loaded at startup.
/// </summary>
internal static class PluginManager
{
    // Default location when nothing else is set — per-user, self-contained.
    private static readonly string DefaultPluginDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".bowire", "plugins");

    /// <summary>Environment-variable name that overrides the default plugin path.</summary>
    public const string PluginDirEnvVar = "BOWIRE_PLUGIN_DIR";

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>
    /// Pick the active plugin directory. Priority order:
    /// <list type="number">
    ///   <item>Explicit path passed via <c>--plugin-dir</c> on the CLI.</item>
    ///   <item>The <c>BOWIRE_PLUGIN_DIR</c> environment variable.</item>
    ///   <item>The default <c>~/.bowire/plugins/</c>.</item>
    /// </list>
    /// Returns the absolute path so install / list / uninstall / load all
    /// agree on the same directory regardless of working-directory drift.
    /// </summary>
    public static string ResolvePluginDir(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(explicitPath);
        var env = Environment.GetEnvironmentVariable(PluginDirEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env);
        return DefaultPluginDir;
    }

    /// <summary>
    /// Install a NuGet package as a Bowire plugin.
    /// Downloads the package, extracts DLLs to ~/.bowire/plugins/{packageId}/
    /// </summary>
    public static async Task<int> InstallAsync(
        string packageId,
        string? version,
        string? pluginDir = null,
        IReadOnlyList<string>? sources = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            Console.WriteLine("  Usage: bowire plugin install <package-id> [--version <version>] [--source <url>] [--plugin-dir <path>]");
            return 2;
        }

        var dir = ResolvePluginDir(pluginDir);
        Directory.CreateDirectory(dir);
        var pluginSubDir = Path.Combine(dir, packageId);

        if (Directory.Exists(pluginSubDir))
        {
            Console.WriteLine($"  Plugin '{packageId}' is already installed. Use 'bowire plugin uninstall {packageId}' first.");
            return 1;
        }

        Console.WriteLine($"  Installing {packageId}" +
            (version is null ? "" : $" @ {version}") + "...");

        try
        {
            var result = await NuGetPackageInstaller.InstallAsync(
                packageId,
                version,
                dir,
                sources ?? [],
                NuGetNull.Instance,
                ct);

            await File.WriteAllTextAsync(Path.Combine(result.TargetDir, "plugin.json"),
                JsonSerializer.Serialize(new
                {
                    packageId,
                    // `version` keeps what the user requested — either
                    // a concrete version or "latest" when they didn't
                    // pin. `resolvedVersion` is what the installer
                    // actually landed on; `plugin update` compares
                    // against it.
                    version = version ?? "latest",
                    resolvedVersion = result.ResolvedVersion,
                    installedAt = DateTimeOffset.UtcNow.ToString("o"),
                    // Record the effective source list — fall back to
                    // nuget.org when the caller didn't configure any,
                    // so `plugin list --verbose` shows something
                    // meaningful instead of a blank entry.
                    sources = sources is { Count: > 0 }
                        ? sources
                        : [NuGetPackageInstaller.DefaultSource],
                    files = Directory.GetFiles(result.TargetDir)
                        .Select(Path.GetFileName)
                        .ToArray()
                }, IndentedJson), ct);

            Console.WriteLine(
                $"  Installed {packageId} {result.ResolvedVersion} " +
                $"({result.FilesWritten} file(s), {result.PackagesResolved} package(s)) -> {result.TargetDir}");
            return 0;
        }
        catch (Exception ex)
        {
            // Failed halfway — drop the partially-written plugin dir
            // so the next attempt isn't blocked by the "already
            // installed" check.
            try { Directory.Delete(pluginSubDir, recursive: true); } catch { /* best-effort */ }
            Console.WriteLine($"  Failed to install {packageId}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Install a plugin from a local <c>.nupkg</c> file rather than
    /// from a NuGet feed. The package's id and version come from its
    /// embedded <c>.nuspec</c>, so the caller doesn't need to know
    /// either ahead of time. When <paramref name="sources"/> is set,
    /// transitive dependencies are pulled from those feeds (typically
    /// another local folder for fully-offline installs); otherwise
    /// any unmet deps are surfaced in the output for the user to
    /// install separately.
    /// </summary>
    public static async Task<int> InstallFromFileAsync(
        string nupkgPath,
        string? pluginDir = null,
        IReadOnlyList<string>? sources = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nupkgPath))
        {
            Console.WriteLine("  Usage: bowire plugin install --file <path-to-package.nupkg> [--source <url>] [--plugin-dir <path>]");
            return 2;
        }

        if (!File.Exists(nupkgPath))
        {
            Console.WriteLine($"  File not found: {nupkgPath}");
            return 1;
        }

        var dir = ResolvePluginDir(pluginDir);
        Directory.CreateDirectory(dir);

        // We can't run the "already installed" guard before reading
        // the nuspec, since the caller might not know the package id
        // (that's part of the value of --file). So we peek at the
        // package, *then* check, *then* extract.
        string peekedId;
        try
        {
            await using var peekStream = File.OpenRead(nupkgPath);
            using var peekArchive = new NuGet.Packaging.PackageArchiveReader(peekStream);
            peekedId = peekArchive.GetIdentity().Id;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed to read {nupkgPath}: {ex.Message}");
            return 1;
        }

        var pluginSubDir = Path.Combine(dir, peekedId);
        if (Directory.Exists(pluginSubDir))
        {
            Console.WriteLine($"  Plugin '{peekedId}' is already installed. Use 'bowire plugin uninstall {peekedId}' first.");
            return 1;
        }

        Console.WriteLine($"  Installing {peekedId} from {nupkgPath}...");

        try
        {
            var result = await NuGetPackageInstaller.InstallFromFileAsync(
                nupkgPath,
                dir,
                sources ?? [],
                NuGetNull.Instance,
                ct);

            await File.WriteAllTextAsync(Path.Combine(result.TargetDir, "plugin.json"),
                JsonSerializer.Serialize(new
                {
                    packageId = result.PackageId,
                    version = result.ResolvedVersion,
                    resolvedVersion = result.ResolvedVersion,
                    installedAt = DateTimeOffset.UtcNow.ToString("o"),
                    // Source list records the actual file path so
                    // `plugin list --verbose` shows where the plugin
                    // came from. Falls back to the configured feeds
                    // when the user mixed --file with --source for
                    // dep resolution.
                    sources = sources is { Count: > 0 }
                        ? new[] { Path.GetFullPath(nupkgPath) }.Concat(sources).ToArray()
                        : new[] { Path.GetFullPath(nupkgPath) },
                    files = Directory.GetFiles(result.TargetDir)
                        .Select(Path.GetFileName)
                        .ToArray()
                }, IndentedJson), ct);

            Console.WriteLine(
                $"  Installed {result.PackageId} {result.ResolvedVersion} " +
                $"({result.FilesWritten} file(s), {result.PackagesResolved} package(s)) -> {result.TargetDir}");

            if (result.UnmetDependencies.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  warning: the package has runtime dependencies that weren't installed:");
                foreach (var dep in result.UnmetDependencies)
                {
                    Console.WriteLine($"    - {dep}");
                }
                Console.WriteLine();
                Console.WriteLine("  Re-run with --source pointing at a feed that has them, or install");
                Console.WriteLine("  each dep separately via 'bowire plugin install --file <dep.nupkg>'.");
            }

            return 0;
        }
        catch (Exception ex)
        {
            // Failed halfway — drop the partially-written plugin dir
            // so the next attempt isn't blocked by the "already
            // installed" check.
            try { Directory.Delete(pluginSubDir, recursive: true); } catch { /* best-effort */ }
            Console.WriteLine($"  Failed to install from {nupkgPath}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Download a plugin and every transitive dependency as <c>.nupkg</c>
    /// files into <paramref name="outputDir"/>. The resulting folder is
    /// a complete offline bundle: transfer it to an air-gapped host
    /// and install via <c>bowire plugin install --file &lt;root&gt;.nupkg
    /// --source &lt;outputDir&gt;</c>.
    /// </summary>
    public static async Task<int> DownloadAsync(
        string packageId,
        string? version,
        string? outputDir,
        IReadOnlyList<string>? sources = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            Console.WriteLine("  Usage: bowire plugin download <package-id> [--version <version>] [--source <url>] --output <dir>");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            Console.WriteLine("  Missing --output. Pass a directory where the .nupkg files should land.");
            Console.WriteLine("  Example: bowire plugin download MyCompany.Plugin --output ./offline-bundle/");
            return 2;
        }

        var fullOut = Path.GetFullPath(outputDir);
        Console.WriteLine($"  Downloading {packageId}" +
            (version is null ? "" : $" @ {version}") +
            $" + dependencies into {fullOut}...");

        try
        {
            var result = await NuGetPackageInstaller.DownloadAsync(
                packageId,
                version,
                fullOut,
                sources ?? [],
                NuGetNull.Instance,
                ct);

            Console.WriteLine();
            Console.WriteLine($"  Bundled {result.Packages.Count} package(s) ({result.RootPackageId} {result.RootVersion} + transitive deps):");
            foreach (var pkg in result.Packages)
            {
                var sizeKb = pkg.Bytes / 1024.0;
                Console.WriteLine($"    {pkg.PackageId} {pkg.Version}  ({sizeKb:N1} KB)");
            }
            Console.WriteLine();
            Console.WriteLine($"  Output: {result.OutputDir}");
            Console.WriteLine();
            Console.WriteLine("  To install on the offline host:");
            Console.WriteLine($"    bowire plugin install --file <root>.nupkg --source <outputDir>");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed to download {packageId}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>List installed plugins.</summary>
    public static int List(string? pluginDir = null, bool verbose = false)
    {
        var dir = ResolvePluginDir(pluginDir);
        if (!Directory.Exists(dir))
        {
            Console.WriteLine("  No plugins installed.");
            Console.WriteLine($"  Plugin directory: {dir}");
            return 0;
        }

        var dirs = Directory.GetDirectories(dir);
        if (dirs.Length == 0)
        {
            Console.WriteLine("  No plugins installed.");
            Console.WriteLine($"  Plugin directory: {dir}");
            return 0;
        }

        Console.WriteLine($"  Installed plugins ({dirs.Length}):");
        Console.WriteLine();
        foreach (var pluginPath in dirs)
        {
            var name = Path.GetFileName(pluginPath);
            var meta = ReadPluginMetadata(pluginPath);
            var dllCount = Directory.GetFiles(pluginPath, "*.dll").Length;

            if (!verbose)
            {
                Console.WriteLine($"    {name}  v{meta.DisplayVersion}  ({dllCount} files)");
                continue;
            }

            Console.WriteLine($"    {name}  v{meta.DisplayVersion}");
            if (!string.IsNullOrEmpty(meta.ResolvedVersion) &&
                !string.Equals(meta.ResolvedVersion, meta.RequestedVersion, StringComparison.Ordinal))
            {
                Console.WriteLine($"      requested:  {meta.RequestedVersion ?? "<unset>"}");
                Console.WriteLine($"      resolved:   {meta.ResolvedVersion}");
            }
            if (!string.IsNullOrEmpty(meta.InstalledAt))
                Console.WriteLine($"      installed:  {meta.InstalledAt}");
            if (meta.Sources.Count > 0)
                Console.WriteLine($"      sources:    {string.Join(", ", meta.Sources)}");
            var files = Directory.GetFiles(pluginPath, "*.dll")
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            if (files.Count > 0)
            {
                Console.WriteLine($"      files:      {string.Join(", ", files)}");
            }
        }
        Console.WriteLine();
        Console.WriteLine($"  Plugin directory: {dir}");
        return 0;
    }

    // Canonical view of the plugin.json fields that List --verbose needs.
    // Pulled out so we can tolerate partial / older manifests (pre-update
    // installs didn't record resolvedVersion or sources).
    private sealed record PluginMetadata(
        string? RequestedVersion,
        string? ResolvedVersion,
        string? InstalledAt,
        IReadOnlyList<string> Sources)
    {
        public string DisplayVersion =>
            !string.IsNullOrEmpty(ResolvedVersion) ? ResolvedVersion
            : !string.IsNullOrEmpty(RequestedVersion) ? RequestedVersion
            : "unknown";
    }

    private static PluginMetadata ReadPluginMetadata(string pluginPath)
    {
        var metadataFile = Path.Combine(pluginPath, "plugin.json");
        if (!File.Exists(metadataFile))
            return new PluginMetadata(null, null, null, Array.Empty<string>());

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metadataFile));
            var root = doc.RootElement;
            var requested = root.TryGetProperty("version", out var v)
                ? v.GetString() : null;
            var resolved = root.TryGetProperty("resolvedVersion", out var rv)
                ? rv.GetString() : null;
            var installedAt = root.TryGetProperty("installedAt", out var ia)
                ? ia.GetString() : null;

            var sources = new List<string>();
            if (root.TryGetProperty("sources", out var src) && src.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in src.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String &&
                        el.GetString() is { Length: > 0 } s)
                    {
                        sources.Add(s);
                    }
                }
            }

            return new PluginMetadata(requested, resolved, installedAt, sources);
        }
        catch
        {
            return new PluginMetadata(null, null, null, Array.Empty<string>());
        }
    }

    /// <summary>
    /// Update an installed plugin to either the latest version on the
    /// configured sources or a caller-specified target version. No-op
    /// when the installed resolved-version already matches.
    /// </summary>
    public static async Task<int> UpdateAsync(
        string packageId,
        string? targetVersion,
        string? pluginDir = null,
        IReadOnlyList<string>? sources = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            Console.WriteLine(
                "  Usage: bowire plugin update <package-id> [--version <version>] [--source <url>] [--plugin-dir <path>]");
            return 2;
        }

        var dir = ResolvePluginDir(pluginDir);
        var pluginSubDir = Path.Combine(dir, packageId);
        if (!Directory.Exists(pluginSubDir))
        {
            Console.WriteLine(
                $"  Plugin '{packageId}' is not installed. Run 'bowire plugin install {packageId}' first.");
            return 1;
        }

        var installedVersion = ReadResolvedVersion(pluginSubDir);

        var resolved = await NuGetPackageInstaller.ResolveAsync(
            packageId, targetVersion, sources ?? [], NuGetNull.Instance, ct);
        if (resolved is null)
        {
            Console.WriteLine(
                $"  Failed to resolve {packageId}" +
                (targetVersion is null ? "" : $" @ {targetVersion}") +
                " on the configured source(s).");
            return 1;
        }

        if (!string.IsNullOrEmpty(installedVersion) &&
            string.Equals(installedVersion, resolved.Version, StringComparison.Ordinal))
        {
            Console.WriteLine($"  {packageId} is already at {resolved.Version}.");
            return 0;
        }

        Console.WriteLine(
            $"  Updating {packageId} {installedVersion ?? "<unknown>"} -> {resolved.Version}...");

        // Drop the old install so the regular install path can land
        // the new copy. Fine to reuse InstallAsync here — the "already
        // installed" guard sees a clean slate.
        try { Directory.Delete(pluginSubDir, recursive: true); }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed to remove {pluginSubDir}: {ex.Message}");
            return 1;
        }

        return await InstallAsync(packageId, resolved.Version, pluginDir, sources, ct);
    }

    /// <summary>
    /// Update every installed plugin. Skips plugins that are already at
    /// the latest resolvable version. Returns the first non-zero exit
    /// code encountered (so CI notices partial failures) but keeps
    /// processing the rest.
    /// </summary>
    public static async Task<int> UpdateAllAsync(
        string? pluginDir = null,
        IReadOnlyList<string>? sources = null,
        CancellationToken ct = default)
    {
        var dir = ResolvePluginDir(pluginDir);
        if (!Directory.Exists(dir))
        {
            Console.WriteLine("  No plugins installed.");
            Console.WriteLine($"  Plugin directory: {dir}");
            return 0;
        }

        var packages = Directory.GetDirectories(dir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();
        if (packages.Count == 0)
        {
            Console.WriteLine("  No plugins installed.");
            Console.WriteLine($"  Plugin directory: {dir}");
            return 0;
        }

        Console.WriteLine($"  Updating {packages.Count} plugin(s)...");
        Console.WriteLine();

        var worstExit = 0;
        foreach (var pkg in packages)
        {
            var exit = await UpdateAsync(pkg!, targetVersion: null, pluginDir, sources, ct);
            if (exit != 0 && worstExit == 0) worstExit = exit;
        }
        return worstExit;
    }

    // Plugin.json shape: newer installs write `resolvedVersion`, older
    // ones only wrote `version`. Fall back to `version` so pre-update
    // installs still answer.
    private static string? ReadResolvedVersion(string pluginSubDir)
    {
        var metaPath = Path.Combine(pluginSubDir, "plugin.json");
        if (!File.Exists(metaPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            if (doc.RootElement.TryGetProperty("resolvedVersion", out var rv) &&
                rv.ValueKind == JsonValueKind.String)
            {
                var s = rv.GetString();
                if (!string.IsNullOrEmpty(s) &&
                    !string.Equals(s, "unknown", StringComparison.Ordinal))
                {
                    return s;
                }
            }
            if (doc.RootElement.TryGetProperty("version", out var v) &&
                v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrEmpty(s) &&
                    !string.Equals(s, "latest", StringComparison.Ordinal))
                {
                    return s;
                }
            }
        }
        catch { /* ignore malformed plugin.json */ }
        return null;
    }

    /// <summary>
    /// Live-inspect an installed plugin: loads it into a dedicated
    /// <see cref="BowirePluginLoadContext"/> via
    /// <see cref="BowirePluginHost"/> and reports the ALC name, the
    /// resolved shared-prefix list, every loaded assembly, and every
    /// <see cref="IBowireProtocol"/> / <see cref="IBowireProtocolServices"/>
    /// type it found. Has side effects — runs the plugin's module
    /// initializers — so it's separate from the pure-metadata
    /// <see cref="List"/> command.
    /// </summary>
    public static int Inspect(string packageId, string? pluginDir = null)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            Console.WriteLine("  Usage: bowire plugin inspect <package-id> [--plugin-dir <path>]");
            return 2;
        }

        var dir = ResolvePluginDir(pluginDir);
        var pluginSubDir = Path.Combine(dir, packageId);
        if (!Directory.Exists(pluginSubDir))
        {
            Console.WriteLine(
                $"  Plugin '{packageId}' is not installed. Run 'bowire plugin install {packageId}' first.");
            return 1;
        }

        var meta = ReadPluginMetadata(pluginSubDir);

        // One-shot host for this inspect call. No Unload — the process
        // exits right after printing, so keeping the context loaded is
        // cheaper than asking the GC to collect it.
        var host = new BowirePluginHost();
        BowirePluginLoadContext ctx;
        try
        {
            ctx = host.Load(pluginSubDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed to load plugin '{packageId}': {ex.Message}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"  {packageId}  v{meta.DisplayVersion}");
        // Plain ASCII rule — Unicode dashes render as garbage on
        // Windows consoles that aren't in UTF-8 output-encoding mode.
        Console.WriteLine($"  {new string('-', packageId.Length + meta.DisplayVersion.Length + 4)}");
        Console.WriteLine();

        Console.WriteLine($"  Directory:       {pluginSubDir}");
        if (!string.IsNullOrEmpty(meta.InstalledAt))
            Console.WriteLine($"  Installed:       {meta.InstalledAt}");
        if (meta.Sources.Count > 0)
            Console.WriteLine($"  Sources:         {string.Join(", ", meta.Sources)}");

        Console.WriteLine();
        Console.WriteLine("  Load context");
        Console.WriteLine($"    name:          {ctx.Name}");
        Console.WriteLine($"    collectible:   {ctx.IsCollectible}");

        var loadedAssemblies = ctx.Assemblies.ToList();
        Console.WriteLine($"    assemblies:    {loadedAssemblies.Count}");
        foreach (var asm in loadedAssemblies.OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
        {
            var name = asm.GetName();
            Console.WriteLine($"      {name.Name} {name.Version}");
        }

        Console.WriteLine();
        Console.WriteLine("  Bowire contract implementations");
        var protocols = FindImplementationsOf<IBowireProtocol>(loadedAssemblies);
        var configs = FindImplementationsOf<IBowireProtocolServices>(loadedAssemblies);
        // Mock-emitter contribution is what bowire mock picks up via
        // EnumeratePluginServices; list it here so `plugin inspect` is
        // the single source of truth for "what does this plugin give
        // the host." Plugins that only ship a protocol don't print
        // this line.
        var emitters = FindImplementationsOf<Kuestenlogik.Bowire.Mocking.IBowireMockEmitter>(loadedAssemblies);

        if (protocols.Count == 0 && configs.Count == 0 && emitters.Count == 0)
        {
            Console.WriteLine("    (none found — is this actually a Bowire protocol plugin?)");
        }
        else
        {
            foreach (var t in protocols)
                Console.WriteLine($"    IBowireProtocol          {t.FullName}");
            foreach (var t in configs)
                Console.WriteLine($"    IBowireProtocolServices  {t.FullName}");
            foreach (var t in emitters)
                Console.WriteLine($"    IBowireMockEmitter       {t.FullName}");
        }
        Console.WriteLine();
        return 0;
    }

    // Reflect over the context's loaded assemblies for concrete
    // implementations of the given contract interface. Swallows per-
    // assembly ReflectionTypeLoadException so one broken DLL doesn't
    // hide every other result.
    private static List<Type> FindImplementationsOf<T>(IReadOnlyList<Assembly> assemblies)
    {
        var contract = typeof(T);
        var hits = new List<Type>();
        foreach (var asm in assemblies)
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
                hits.Add(type);
            }
        }
        return hits;
    }

    /// <summary>Uninstall a plugin.</summary>
    public static int Uninstall(string packageId, string? pluginDir = null)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            Console.WriteLine("  Usage: bowire plugin uninstall <package-id> [--plugin-dir <path>]");
            return 2;
        }

        var dir = ResolvePluginDir(pluginDir);
        var pluginSubDir = Path.Combine(dir, packageId);
        if (!Directory.Exists(pluginSubDir))
        {
            Console.WriteLine($"  Plugin '{packageId}' is not installed.");
            return 1;
        }

        Directory.Delete(pluginSubDir, recursive: true);
        Console.WriteLine($"  Uninstalled {packageId}.");
        return 0;
    }

    /// <summary>
    /// Load all plugin assemblies from the resolved plugin directory.
    /// Each package-subdirectory gets its own
    /// <see cref="BowirePluginLoadContext"/> so plugin-private deps
    /// don't collide with each other; shared contract assemblies
    /// (<c>Kuestenlogik.Bowire*</c>, <c>System.*</c>, <c>Microsoft.*</c>)
    /// delegate to the default ALC so the host's interface types stay
    /// type-identity-identical across every context.
    /// </summary>
    /// <param name="pluginDir">
    /// Explicit directory, usually from <c>--plugin-dir</c>. When
    /// <c>null</c>, <see cref="ResolvePluginDir"/> falls back to
    /// <c>BOWIRE_PLUGIN_DIR</c> and then to <c>~/.bowire/plugins/</c>.
    /// </param>
    public static void LoadPlugins(string? pluginDir = null)
    {
        var dir = ResolvePluginDir(pluginDir);
        if (!Directory.Exists(dir)) return;

        foreach (var subDir in Directory.GetDirectories(dir))
        {
            BowirePluginLoadContext ctx;
            try { ctx = new BowirePluginLoadContext(subDir); }
            catch { continue; }
            s_pluginContexts.Add(ctx);

            foreach (var dll in Directory.GetFiles(subDir, "*.dll"))
            {
                try
                {
                    ctx.LoadFromAssemblyPath(Path.GetFullPath(dll));
                }
                catch
                {
                    // Skip DLLs that fail to load
                }
            }
        }
    }

    // Tracks plugin contexts created by LoadPlugins so extension-point
    // callers (mock emitters, future replayers, ...) can enumerate
    // them without re-walking the plugin directory.
    private static readonly List<BowirePluginLoadContext> s_pluginContexts = new();

    /// <summary>
    /// Instantiate every <typeparamref name="T"/> contributed by a
    /// loaded plugin, scanning all plugin ALCs. Types need a public
    /// parameterless constructor — matching the discovery contract
    /// <c>IBowireProtocol</c> already uses. Extension authors implement
    /// their interface, ship a DLL, and Bowire picks it up at startup
    /// via this walk.
    /// </summary>
    /// <remarks>
    /// Exceptions during type scan or instantiation are swallowed with
    /// a stderr warning so a single broken plugin doesn't take down
    /// the host. Returns an empty list when no plugins are loaded.
    /// </remarks>
    public static List<T> EnumeratePluginServices<T>() where T : class
    {
        var results = new List<T>();
        var contract = typeof(T);
        foreach (var ctx in s_pluginContexts)
        {
            foreach (var asm in ctx.Assemblies)
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
                    try
                    {
                        if (Activator.CreateInstance(type) is T instance)
                        {
                            results.Add(instance);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"  warning: failed to instantiate plugin type '{type.FullName}': {ex.Message}");
                    }
                }
            }
        }
        return results;
    }

    /// <summary>Show plugin subcommand help.</summary>
    public static int ShowHelp()
    {
        Console.WriteLine($"""
            bowire plugin -- Manage protocol plugins

            Usage: bowire plugin <command> [options]

            Commands:
              install <package-id>   Install a protocol plugin from NuGet.
              install --file <path>  Install a protocol plugin from a local .nupkg.
                                     Useful for air-gapped hosts or enterprise approval
                                     flows: download the package once, then transfer the
                                     file. Package id + version are read from the .nuspec.
              download <package-id>  Download a plugin + every transitive dependency as
                                     .nupkg files into --output. Pair with
                                     `install --file <root>.nupkg --source <outputDir>`
                                     on the offline host to complete the air-gapped flow.
              list                   List installed plugins.
              uninstall <package-id> Remove a plugin.
              update [<package-id>]  Update one installed plugin (or all installed
                                     plugins when no id is given) to the latest
                                     version on the configured source(s).
              inspect <package-id>   Load the plugin into a dedicated ALC and print
                                     its load-context name, loaded assemblies, and
                                     discovered IBowireProtocol implementations.
                                     Side-effecting (runs module initializers).

            Options:
              --verbose, -v          Expand `plugin list` output with the resolved
                                     version, install timestamp, sources, and DLL
                                     list per plugin. No effect on other commands.
              --file <path>          Install from a local .nupkg file instead of
                                     resolving the package over the network.
              --output, -o <dir>     Output directory for `plugin download`. Holds
                                     the root .nupkg + every transitive dep so the
                                     folder can be transferred and used as a local
                                     NuGet source on the install host.
              --version <version>    Specific NuGet package version. On install,
                                     pins the initial version. On update, moves
                                     the plugin to exactly that version. On download,
                                     pins which version is bundled. Ignored when
                                     --file is supplied (version comes from the .nuspec).
              --source,  -s <url>    NuGet feed URL to resolve from (install / update,
                                     repeatable). Defaults to nuget.org when unset.
                                     Local folders work too — point at a directory
                                     of .nupkg files for fully-offline installs.
                                     Also bindable as the "Bowire:Plugin:Sources"
                                     array in appsettings.json.
              --plugin-dir <path>    Override the plugin directory for this command.
                                     Also accepted at the top level (applies to plugin
                                     loading in every bowire subcommand).

            Environment:
              {PluginDirEnvVar}      Default plugin directory (used when --plugin-dir is
                                     not passed). Falls back to ~/.bowire/plugins/.

            Examples:
              bowire plugin install MyCompany.Bowire.Protocol.Mqtt
              bowire plugin install MyCompany.Bowire.Protocol.Mqtt --version 1.2.0
              bowire plugin install MyCompany.Internal.Plugin --source https://nuget.mycorp.internal/v3/index.json
              bowire plugin install --file ./MyCompany.Bowire.Protocol.Mqtt.1.2.0.nupkg
              bowire plugin install --file ./Plugin.nupkg --source ./offline-feed/
              bowire plugin install MyCompany.Internal.Plugin --source ./local-feed/

              # Air-gapped flow — download on a connected machine, transfer the
              # folder, then install offline.
              bowire plugin download MyCompany.Bowire.Protocol.Mqtt --output ./bundle/
              bowire plugin install --file ./bundle/mycompany.bowire.protocol.mqtt.1.2.0.nupkg \\
                                     --source ./bundle/

              bowire plugin list
              bowire plugin uninstall MyCompany.Bowire.Protocol.Mqtt
              bowire plugin list --plugin-dir ./my-plugins
              bowire plugin update MyCompany.Bowire.Protocol.Mqtt
              bowire plugin update              (updates every installed plugin)
              bowire plugin inspect MyCompany.Bowire.Protocol.Mqtt
            """);
        return 0;
    }

}
