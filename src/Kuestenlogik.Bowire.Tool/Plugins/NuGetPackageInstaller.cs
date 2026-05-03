// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Kuestenlogik.Bowire.App.Plugins;

/// <summary>
/// Downloads a NuGet package + its transitive deps via
/// <c>NuGet.Protocol</c> and unpacks the right-TFM lib DLLs into a
/// plugin directory. Replaces the earlier "synthesise a csproj and
/// spawn dotnet restore/build" approach, which required the .NET SDK
/// at install time and was multiple orders of magnitude slower than a
/// direct download.
/// </summary>
/// <remarks>
/// <para>
/// For each requested package:
/// </para>
/// <list type="number">
///   <item>Resolve the version — explicit pin, or the latest stable
///   each source advertises.</item>
///   <item>Download the <c>.nupkg</c> (it's a ZIP) and read its
///   <c>.nuspec</c> for dependencies.</item>
///   <item>Pick the best-matching lib folder for the host's target
///   framework (<c>net10.0</c>) via NuGet's own framework-reducer.</item>
///   <item>Copy those DLLs into <c>&lt;pluginDir&gt;/&lt;packageId&gt;/</c>,
///   skipping assemblies that are already loaded by the Bowire host
///   (<c>Kuestenlogik.Bowire*</c>, <c>System.*</c>, <c>Microsoft.*</c>,
///   <c>NETStandard.*</c>).</item>
///   <item>Recurse into transitive deps, deduping by <c>packageId</c>.</item>
/// </list>
/// <para>
/// Source list: whatever the caller supplies, or nuget.org as a
/// fallback. CLI <c>--source</c> flags and
/// <c>Bowire:Plugin:Sources</c> appsettings entries both feed in here.
/// </para>
/// </remarks>
internal static class NuGetPackageInstaller
{
    /// <summary>Default feed when the caller doesn't supply any.</summary>
    public const string DefaultSource = "https://api.nuget.org/v3/index.json";

    /// <summary>Host TFM — matches the <c>TargetFramework</c> of the Bowire tool binary.</summary>
    private static readonly NuGetFramework s_hostFramework = NuGetFramework.Parse("net10.0");

    /// <summary>
    /// Install <paramref name="packageId"/> (optionally pinned to
    /// <paramref name="version"/>) + every transitive runtime dependency
    /// into <paramref name="pluginDir"/>/<paramref name="packageId"/>.
    /// Returns the number of DLLs written.
    /// </summary>
    public static async Task<InstallResult> InstallAsync(
        string packageId,
        string? version,
        string pluginDir,
        IReadOnlyList<string> sources,
        NuGet.Common.ILogger logger,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageId);
        ArgumentException.ThrowIfNullOrEmpty(pluginDir);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(logger);

        var targetDir = Path.Combine(pluginDir, packageId);
        Directory.CreateDirectory(targetDir);

        // Build the source repository list once; nuget.org is the default
        // when the caller doesn't specify anything.
        var effectiveSources = sources.Count > 0
            ? sources
            : [DefaultSource];
        var repositories = effectiveSources
            .Select(url => Repository.Factory.GetCoreV3(new PackageSource(url)))
            .ToList();

        using var cache = new SourceCacheContext();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filesWritten = 0;
        NuGetVersion? rootResolved = null;

        await InstallRecursiveAsync(
            packageId, ParseVersion(version), repositories, cache,
            targetDir, visited, logger,
            onFileWritten: () => filesWritten++,
            onRootResolved: v => rootResolved = v,
            ct);

        return new InstallResult(
            packageId,
            rootResolved?.ToNormalizedString() ?? "unknown",
            targetDir,
            filesWritten,
            visited.Count);
    }

    /// <summary>
    /// Resolve the best-match version for <paramref name="packageId"/>
    /// across the configured <paramref name="sources"/> without downloading
    /// anything. Used by <c>bowire plugin update</c> to compare the
    /// installed version against what the feeds currently advertise.
    /// </summary>
    internal static async Task<ResolvedPackageInfo?> ResolveAsync(
        string packageId,
        string? requestedVersion,
        IReadOnlyList<string> sources,
        NuGet.Common.ILogger logger,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageId);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(logger);

        var effectiveSources = sources.Count > 0 ? sources : [DefaultSource];
        var repositories = effectiveSources
            .Select(url => Repository.Factory.GetCoreV3(new PackageSource(url)))
            .ToList();

        using var cache = new SourceCacheContext();
        var (repo, version) = await ResolveVersionAsync(
            packageId, ParseVersion(requestedVersion), repositories, cache, logger, ct);

        return repo is null || version is null
            ? null
            : new ResolvedPackageInfo(
                packageId,
                version.ToNormalizedString(),
                repo.PackageSource.Source);
    }

    internal sealed record InstallResult(
        string PackageId,
        string ResolvedVersion,
        string TargetDir,
        int FilesWritten,
        int PackagesResolved);

    /// <summary>Feed-URL + version pair returned by <see cref="ResolveAsync"/>.</summary>
    internal sealed record ResolvedPackageInfo(string PackageId, string Version, string SourceUrl);

    /// <summary>
    /// Download <paramref name="packageId"/> + every transitive
    /// runtime dependency as raw <c>.nupkg</c> files into
    /// <paramref name="outputDir"/>. Companion to
    /// <see cref="InstallFromFileAsync"/>: run this on an online host
    /// to build an offline bundle, transfer the folder, then install
    /// from it on the air-gapped machine via
    /// <c>bowire plugin install --file &lt;root&gt;.nupkg --source &lt;outputDir&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Uses the same host-provided filter as <see cref="InstallAsync"/>
    /// — Bowire's contract assemblies and the .NET framework deps
    /// don't get bundled because they're already in the install host.
    /// File names follow the standard NuGet flat-feed convention
    /// (<c>&lt;id&gt;.&lt;version&gt;.nupkg</c>) so the resulting folder is a
    /// drop-in NuGet v2 source — the install path's recursive resolver
    /// can read it without any extra index file.
    /// </remarks>
    public static async Task<DownloadResult> DownloadAsync(
        string packageId,
        string? version,
        string outputDir,
        IReadOnlyList<string> sources,
        NuGet.Common.ILogger logger,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageId);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(logger);

        Directory.CreateDirectory(outputDir);

        var effectiveSources = sources.Count > 0 ? sources : [DefaultSource];
        var repositories = effectiveSources
            .Select(url => Repository.Factory.GetCoreV3(new PackageSource(url)))
            .ToList();

        using var cache = new SourceCacheContext();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packagesDownloaded = new List<DownloadedPackage>();
        NuGetVersion? rootResolved = null;

        await DownloadRecursiveAsync(
            packageId, ParseVersion(version), repositories, cache,
            outputDir, visited, logger,
            onPackageDownloaded: packagesDownloaded.Add,
            onRootResolved: v => rootResolved = v,
            ct);

        return new DownloadResult(
            packageId,
            rootResolved?.ToNormalizedString() ?? "unknown",
            Path.GetFullPath(outputDir),
            packagesDownloaded);
    }

    /// <summary>
    /// Outcome of <see cref="DownloadAsync"/> — root id + version,
    /// resolved bundle directory, and the full list of <c>.nupkg</c>
    /// files written (root + every transitive dep).
    /// </summary>
    internal sealed record DownloadResult(
        string RootPackageId,
        string RootVersion,
        string OutputDir,
        IReadOnlyList<DownloadedPackage> Packages);

    /// <summary>One row in <see cref="DownloadResult.Packages"/>.</summary>
    internal sealed record DownloadedPackage(
        string PackageId,
        string Version,
        string FilePath,
        long Bytes);

    /// <summary>
    /// Recursively walk the dependency graph, downloading each
    /// non-host-provided package as a <c>.nupkg</c> into
    /// <paramref name="outputDir"/>. Mirrors the structure of
    /// <see cref="InstallRecursiveAsync"/> but writes the raw archive
    /// to disk rather than extracting its lib contents.
    /// </summary>
    private static async Task DownloadRecursiveAsync(
        string packageId,
        NuGetVersion? requestedVersion,
        List<SourceRepository> repositories,
        SourceCacheContext cache,
        string outputDir,
        HashSet<string> visited,
        NuGet.Common.ILogger logger,
        Action<DownloadedPackage> onPackageDownloaded,
        Action<NuGetVersion>? onRootResolved,
        CancellationToken ct)
    {
        if (!visited.Add(packageId)) return;
        if (IsHostProvided(packageId)) return;

        var (repo, resolvedVersion) = await ResolveVersionAsync(
            packageId, requestedVersion, repositories, cache, logger, ct);
        if (repo is null || resolvedVersion is null)
        {
            throw new InvalidOperationException(
                $"Package '{packageId}'" +
                (requestedVersion is null ? "" : $" @ {requestedVersion}") +
                $" not found on any of {repositories.Count} configured source(s).");
        }

        onRootResolved?.Invoke(resolvedVersion);

        // Download the .nupkg into a memory stream so we can both
        // write it to disk and read its nuspec for deps without two
        // network round trips.
        var downloader = await repo.GetResourceAsync<FindPackageByIdResource>(ct);
        using var pkgStream = new MemoryStream();
        var downloaded = await downloader.CopyNupkgToStreamAsync(
            packageId, resolvedVersion, pkgStream, cache, logger, ct);
        if (!downloaded)
        {
            throw new InvalidOperationException(
                $"Failed to download {packageId} {resolvedVersion} from {repo.PackageSource.Source}.");
        }

        // NuGet's lowercased-id convention for flat-feed file names
        // (id.version.nupkg, lowercase id) — matches what `nuget
        // install` writes, so the resulting folder is also valid as
        // a v2 flat source for `dotnet restore` if anyone needs that.
#pragma warning disable CA1308 // Flat-feed convention is lowercase by spec, not localised text.
        var fileName = $"{packageId.ToLowerInvariant()}.{resolvedVersion.ToNormalizedString()}.nupkg";
#pragma warning restore CA1308
        var filePath = Path.Combine(outputDir, fileName);
        pkgStream.Position = 0;
        await using (var fileOut = File.Create(filePath))
        {
            await pkgStream.CopyToAsync(fileOut, ct);
        }
        onPackageDownloaded(new DownloadedPackage(
            packageId,
            resolvedVersion.ToNormalizedString(),
            filePath,
            pkgStream.Length));

        // Walk runtime deps for our TFM. Same logic as install: pick
        // the framework-reducer's best-match group and recurse into
        // each non-host-provided dep's min version.
        pkgStream.Position = 0;
        using var archive = new PackageArchiveReader(pkgStream);
        var nuspec = archive.NuspecReader;
        var depReducer = new FrameworkReducer();
        var depGroups = nuspec.GetDependencyGroups().ToList();
        var depFramework = depReducer.GetNearest(
            s_hostFramework,
            depGroups.Select(g => g.TargetFramework));

        if (depFramework is null) return;

        var depGroup = depGroups.First(g => g.TargetFramework.Equals(depFramework));
        foreach (var dep in depGroup.Packages)
        {
            await DownloadRecursiveAsync(
                dep.Id, dep.VersionRange.MinVersion,
                repositories, cache, outputDir, visited, logger,
                onPackageDownloaded, onRootResolved: null, ct);
        }
    }

    private static async Task InstallRecursiveAsync(
        string packageId,
        NuGetVersion? requestedVersion,
        List<SourceRepository> repositories,
        SourceCacheContext cache,
        string targetDir,
        HashSet<string> visited,
        NuGet.Common.ILogger logger,
        Action onFileWritten,
        Action<NuGetVersion>? onRootResolved,
        CancellationToken ct)
    {
        if (!visited.Add(packageId))
        {
            // Already installed earlier in the recursion; skip.
            return;
        }

        if (IsHostProvided(packageId))
        {
            // Already loaded in the Bowire host — don't pull it
            // redundantly. The assembly-scan below would skip the DLL
            // anyway, but skipping the whole download saves time +
            // network.
            return;
        }

        // Find the first source that has the package.
        var (repo, resolvedVersion) = await ResolveVersionAsync(
            packageId, requestedVersion, repositories, cache, logger, ct);

        if (repo is null || resolvedVersion is null)
        {
            throw new InvalidOperationException(
                $"Package '{packageId}'" +
                (requestedVersion is null ? "" : $" @ {requestedVersion}") +
                $" not found on any of {repositories.Count} configured source(s).");
        }

        // onRootResolved fires on the first call — the package the
        // caller actually asked for. Transitive deps resolve too but
        // the caller only needs the root's version to write plugin.json.
        onRootResolved?.Invoke(resolvedVersion);

        // Download the .nupkg into an in-memory stream so we can read
        // its nuspec (for deps) and copy its lib files without hitting
        // disk twice.
        var downloader = await repo.GetResourceAsync<FindPackageByIdResource>(ct);
        using var pkgStream = new MemoryStream();
        var downloaded = await downloader.CopyNupkgToStreamAsync(
            packageId, resolvedVersion, pkgStream, cache, logger, ct);
        if (!downloaded)
        {
            throw new InvalidOperationException(
                $"Failed to download {packageId} {resolvedVersion} from {repo.PackageSource.Source}.");
        }

        pkgStream.Position = 0;
        using var archive = new PackageArchiveReader(pkgStream);

        await ExtractLibsAsync(archive, targetDir, onFileWritten, ct);

        // Recurse into dependencies matching our TFM. Only the runtime
        // deps (dependencies group) need to land in the plugin dir;
        // frameworkReferences / contentFiles / build/ folders are out
        // of scope for Bowire plugins.
        var nuspec = archive.NuspecReader;
        var depReducer = new FrameworkReducer();
        var depGroups = nuspec.GetDependencyGroups().ToList();
        var depFramework = depReducer.GetNearest(
            s_hostFramework,
            depGroups.Select(g => g.TargetFramework));

        if (depFramework is not null)
        {
            var depGroup = depGroups.First(g => g.TargetFramework.Equals(depFramework));
            foreach (var dep in depGroup.Packages)
            {
                // Use the dep's min version when pinning; NuGet's Allow
                // policy default is to accept anything at or above it,
                // so we just take the floor.
                await InstallRecursiveAsync(
                    dep.Id, dep.VersionRange.MinVersion,
                    repositories, cache, targetDir, visited, logger,
                    onFileWritten, onRootResolved: null, ct);
            }
        }
    }

    /// <summary>
    /// Pick the best <c>lib/&lt;tfm&gt;/</c> folder for the host's
    /// framework and copy its DLLs into <paramref name="targetDir"/>.
    /// Skips assemblies the host already provides (Kuestenlogik.Bowire*,
    /// System.*, Microsoft.*, NETStandard.*) so plugin packages don't
    /// drag along Bowire's contract DLLs and break type identity in
    /// the load context.
    /// </summary>
    private static async Task ExtractLibsAsync(
        PackageArchiveReader archive,
        string targetDir,
        Action onFileWritten,
        CancellationToken ct)
    {
        var libItemGroups = await archive.GetLibItemsAsync(ct);
        var reducer = new FrameworkReducer();
        var bestFramework = reducer.GetNearest(
            s_hostFramework,
            libItemGroups.Select(g => g.TargetFramework));

        if (bestFramework is null) return;

        var bestGroup = libItemGroups.First(g => g.TargetFramework.Equals(bestFramework));
        foreach (var relativePath in bestGroup.Items)
        {
            if (!relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

            var fileName = Path.GetFileName(relativePath);
            var assemblyName = Path.GetFileNameWithoutExtension(fileName);
            if (IsHostProvidedAssembly(assemblyName)) continue;

            var outPath = Path.Combine(targetDir, fileName);
            await using (var entry = await archive.GetEntry(relativePath).OpenAsync(ct))
            await using (var outFile = File.Create(outPath))
            {
                await entry.CopyToAsync(outFile, ct);
            }
            onFileWritten();
        }
    }

    /// <summary>
    /// Install a Bowire plugin from a local <c>.nupkg</c> file rather
    /// than resolving it through a NuGet feed. Useful for air-gapped
    /// hosts and enterprise approval flows where the package was
    /// reviewed offline before being copied onto the install host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The package's id and version are read from its embedded
    /// <c>.nuspec</c>; the caller doesn't need to know either ahead of
    /// time. The matching <c>lib/&lt;tfm&gt;/</c> folder is extracted
    /// into <paramref name="pluginDir"/>/<c>&lt;packageId&gt;/</c>.
    /// </para>
    /// <para>
    /// Transitive dependencies are resolved through
    /// <paramref name="sources"/> when supplied — pass a local folder
    /// holding the dep <c>.nupkg</c> files for fully-offline installs,
    /// or any reachable feed for hybrid scenarios. When
    /// <paramref name="sources"/> is empty, dependencies are listed in
    /// the install report but not pulled; the caller can install each
    /// dep with its own <c>--file</c> call.
    /// </para>
    /// </remarks>
    public static async Task<InstallFromFileResult> InstallFromFileAsync(
        string nupkgPath,
        string pluginDir,
        IReadOnlyList<string> sources,
        NuGet.Common.ILogger logger,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(nupkgPath);
        ArgumentException.ThrowIfNullOrEmpty(pluginDir);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(logger);

        if (!File.Exists(nupkgPath))
            throw new FileNotFoundException($"NuGet package not found: {nupkgPath}", nupkgPath);

        // PackageArchiveReader takes ownership of the stream and closes
        // it on Dispose, so a using on both isn't redundant — the inner
        // FileStream needs an explicit Dispose path either way.
        await using var fileStream = File.OpenRead(nupkgPath);
        using var archive = new PackageArchiveReader(fileStream);
        var identity = archive.GetIdentity();

        var packageId = identity.Id;
        var resolvedVersion = identity.Version.ToNormalizedString();
        var targetDir = Path.Combine(pluginDir, packageId);
        Directory.CreateDirectory(targetDir);

        var filesWritten = 0;
        await ExtractLibsAsync(archive, targetDir, () => filesWritten++, ct);

        // Walk transitive deps for our TFM. With sources configured we
        // recurse via the regular install path; without sources we
        // collect a list of unmet dep names so the CLI can surface
        // them. The "host already provides this" filter still applies
        // — Kuestenlogik.Bowire.* + framework deps don't get installed.
        var nuspec = archive.NuspecReader;
        var depReducer = new FrameworkReducer();
        var depGroups = nuspec.GetDependencyGroups().ToList();
        var depFramework = depReducer.GetNearest(
            s_hostFramework,
            depGroups.Select(g => g.TargetFramework));

        var unmetDeps = new List<string>();
        var packagesResolved = 1; // The root package itself.

        if (depFramework is not null)
        {
            var depGroup = depGroups.First(g => g.TargetFramework.Equals(depFramework));
            var pluginDeps = depGroup.Packages
                .Where(d => !IsHostProvided(d.Id))
                .ToList();

            if (pluginDeps.Count > 0)
            {
                if (sources.Count == 0)
                {
                    // No feeds → can't pull transitive deps. List them
                    // so the user knows what they need to also install.
                    unmetDeps.AddRange(pluginDeps.Select(d =>
                        d.VersionRange.MinVersion is { } v
                            ? $"{d.Id} >= {v.ToNormalizedString()}"
                            : d.Id));
                }
                else
                {
                    var repositories = sources
                        .Select(url => Repository.Factory.GetCoreV3(new PackageSource(url)))
                        .ToList();
                    using var cache = new SourceCacheContext();
                    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { packageId };

                    foreach (var dep in pluginDeps)
                    {
                        await InstallRecursiveAsync(
                            dep.Id, dep.VersionRange.MinVersion,
                            repositories, cache, targetDir, visited, logger,
                            onFileWritten: () => filesWritten++,
                            onRootResolved: null, ct);
                    }
                    packagesResolved = visited.Count;
                }
            }
        }

        return new InstallFromFileResult(
            packageId,
            resolvedVersion,
            targetDir,
            filesWritten,
            packagesResolved,
            unmetDeps);
    }

    /// <summary>
    /// Outcome of <see cref="InstallFromFileAsync"/>. Mirrors
    /// <see cref="InstallResult"/> plus the list of dependencies the
    /// installer couldn't pull (because no <c>--source</c> was passed).
    /// </summary>
    internal sealed record InstallFromFileResult(
        string PackageId,
        string ResolvedVersion,
        string TargetDir,
        int FilesWritten,
        int PackagesResolved,
        IReadOnlyList<string> UnmetDependencies);

    private static async Task<(SourceRepository? Repo, NuGetVersion? Version)> ResolveVersionAsync(
        string packageId,
        NuGetVersion? requested,
        List<SourceRepository> repositories,
        SourceCacheContext cache,
        NuGet.Common.ILogger logger,
        CancellationToken ct)
    {
        foreach (var repo in repositories)
        {
            try
            {
                var resource = await repo.GetResourceAsync<FindPackageByIdResource>(ct);
                if (resource is null) continue;

                var versions = (await resource.GetAllVersionsAsync(packageId, cache, logger, ct)).ToList();
                if (versions.Count == 0) continue;

                if (requested is not null)
                {
                    // Exact-match first, then best-match if the user
                    // pinned a range-ish version string we can't honour
                    // precisely.
                    var exact = versions.FirstOrDefault(v => v.Equals(requested));
                    if (exact is not null) return (repo, exact);
                    continue;
                }

                // No pin → pick the latest *stable* when available,
                // otherwise the highest prerelease.
                var stable = versions.Where(v => !v.IsPrerelease).DefaultIfEmpty().Max();
                return (repo, stable ?? versions.Max());
            }
            catch (FatalProtocolException)
            {
                // Auth failure, bad source URL, HTTP 5xx — move on to
                // the next repo. The final "not found" message will
                // make the overall failure obvious.
                continue;
            }
        }
        return (null, null);
    }

    private static NuGetVersion? ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        return NuGetVersion.TryParse(version, out var v) ? v : null;
    }

    // Framework/Bowire-provided packages — don't try to download them,
    // they're either brought by the runtime (System.*/Microsoft.*) or
    // already loaded by the Bowire host (Kuestenlogik.Bowire*).
    private static bool IsHostProvided(string packageId)
    {
        // Framework packages — always loaded in the default ALC.
        if (packageId.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
            packageId.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
            packageId.StartsWith("NETStandard.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(packageId, "NETStandard.Library", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Kuestenlogik.Bowire.* — the CLI tool bakes in the core + first-party
        // plugins (Grpc, Rest, SignalR, Mqtt, ...), but third-party
        // plugins like Kuestenlogik.Bowire.Protocol.Dis don't ride along and
        // have to be installed. Check the running host for the
        // assembly with that name instead of a blanket prefix skip.
        if (packageId.StartsWith("Kuestenlogik.Bowire", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(asm.GetName().Name, packageId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        return false;
    }

    // Same rule applied to individual assemblies at copy-time — even
    // when a dep sneaks through (e.g. a bundled lib/ DLL named the
    // same as a framework assembly), we don't overwrite the host's
    // version.
    private static bool IsHostProvidedAssembly(string assemblyName) =>
        IsHostProvided(assemblyName);
}
