// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.App.Plugins;
using NuGet.Common;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Coverage for <see cref="NuGetPackageInstaller"/>'s offline path. The
/// online entry points (<c>InstallAsync</c>, <c>DownloadAsync</c>,
/// <c>ResolveAsync</c>) talk to nuget.org and live in the integration
/// suite; here we hand-roll a minimal <c>.nupkg</c> archive (the format
/// is just a ZIP with a <c>.nuspec</c>) and feed it to
/// <see cref="NuGetPackageInstaller.InstallFromFileAsync"/>. That
/// reaches the nuspec read, the host-provided dependency filter, the
/// lib-folder framework reducer, and the unmet-dependency reporting
/// branch — none of which need a feed.
/// </summary>
public sealed class NuGetPackageInstallerTests
{
    private static byte[] BuildMinimalNupkg(
        string packageId,
        string version,
        string targetFramework = "net10.0",
        string? dependencyId = null,
        string? dependencyVersion = null,
        bool includeLibDll = true)
        => NuGetPackageInstallerTests_NupkgFactory.Build(
            packageId, version, targetFramework, dependencyId, dependencyVersion, includeLibDll);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("bowire-nupkg-").FullName;
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void DefaultSource_IsNuGetOrgV3()
    {
        // Documented as a stable contract — any change ripples through
        // every plugin.json file (the resolver writes the effective
        // source list there).
        Assert.Equal("https://api.nuget.org/v3/index.json", NuGetPackageInstaller.DefaultSource);
    }

    [Fact]
    public async Task InstallFromFileAsync_NoDeps_ExtractsLibAndReturnsResult()
    {
        using var dir = new TempDir();
        var nupkg = Path.Combine(dir.Path, "MyCo.Plugin.1.0.0.nupkg");
        await File.WriteAllBytesAsync(nupkg,
            BuildMinimalNupkg("MyCo.Plugin", "1.0.0"),
            TestContext.Current.CancellationToken);

        var pluginDir = Path.Combine(dir.Path, "plugins");
        var result = await NuGetPackageInstaller.InstallFromFileAsync(
            nupkg, pluginDir, sources: [], NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal("MyCo.Plugin", result.PackageId);
        Assert.Equal("1.0.0", result.ResolvedVersion);
        Assert.Equal(1, result.FilesWritten);
        Assert.Empty(result.UnmetDependencies);
        Assert.True(File.Exists(Path.Combine(pluginDir, "MyCo.Plugin", "Stub.dll")));
    }

    [Fact]
    public async Task InstallFromFileAsync_WithUnknownDep_ReportsUnmet()
    {
        using var dir = new TempDir();
        var nupkg = Path.Combine(dir.Path, "MyCo.Plugin.1.0.0.nupkg");
        await File.WriteAllBytesAsync(nupkg,
            BuildMinimalNupkg("MyCo.Plugin", "1.0.0",
                dependencyId: "ThirdParty.Lib", dependencyVersion: "2.5.0"),
            TestContext.Current.CancellationToken);

        var pluginDir = Path.Combine(dir.Path, "plugins");
        var result = await NuGetPackageInstaller.InstallFromFileAsync(
            nupkg, pluginDir, sources: [], NullLogger.Instance,
            TestContext.Current.CancellationToken);

        // No --source → the dependency lands in UnmetDependencies with
        // the "Id >= Version" hint format.
        Assert.Single(result.UnmetDependencies);
        Assert.Contains("ThirdParty.Lib", result.UnmetDependencies[0], StringComparison.Ordinal);
        Assert.Contains("2.5.0", result.UnmetDependencies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallFromFileAsync_WithUnpinnedDep_ReportsBareIdInUnmet()
    {
        using var dir = new TempDir();
        var nupkg = Path.Combine(dir.Path, "MyCo.Plugin.1.0.0.nupkg");
        await File.WriteAllBytesAsync(nupkg,
            BuildMinimalNupkg("MyCo.Plugin", "1.0.0",
                dependencyId: "Floating.Lib", dependencyVersion: null),
            TestContext.Current.CancellationToken);

        var pluginDir = Path.Combine(dir.Path, "plugins");
        var result = await NuGetPackageInstaller.InstallFromFileAsync(
            nupkg, pluginDir, sources: [], NullLogger.Instance,
            TestContext.Current.CancellationToken);

        // Dep without an explicit version → the formatter falls through
        // to the bare id rather than the ">= version" form.
        Assert.Single(result.UnmetDependencies);
        Assert.Equal("Floating.Lib", result.UnmetDependencies[0]);
    }

    [Fact]
    public async Task InstallFromFileAsync_WithHostProvidedDep_FiltersItOut()
    {
        using var dir = new TempDir();
        var nupkg = Path.Combine(dir.Path, "MyCo.Plugin.1.0.0.nupkg");
        // Microsoft.Extensions.Logging is "host-provided" — the
        // installer's filter drops it from UnmetDependencies because it
        // ships with the runtime.
        await File.WriteAllBytesAsync(nupkg,
            BuildMinimalNupkg("MyCo.Plugin", "1.0.0",
                dependencyId: "Microsoft.Extensions.Logging", dependencyVersion: "9.0.0"),
            TestContext.Current.CancellationToken);

        var pluginDir = Path.Combine(dir.Path, "plugins");
        var result = await NuGetPackageInstaller.InstallFromFileAsync(
            nupkg, pluginDir, sources: [], NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Empty(result.UnmetDependencies);
    }

    [Fact]
    public async Task InstallFromFileAsync_NoLibDir_LeavesPluginDirEmpty()
    {
        using var dir = new TempDir();
        var nupkg = Path.Combine(dir.Path, "Empty.Plugin.1.0.0.nupkg");
        await File.WriteAllBytesAsync(nupkg,
            BuildMinimalNupkg("Empty.Plugin", "1.0.0", includeLibDll: false),
            TestContext.Current.CancellationToken);

        var pluginDir = Path.Combine(dir.Path, "plugins");
        var result = await NuGetPackageInstaller.InstallFromFileAsync(
            nupkg, pluginDir, sources: [], NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.FilesWritten);
        Assert.Equal(1, result.PackagesResolved);
    }

    [Fact]
    public async Task InstallFromFileAsync_NullArguments_Throw()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NuGetPackageInstaller.InstallFromFileAsync(
                "", "anywhere", [], NullLogger.Instance,
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NuGetPackageInstaller.InstallFromFileAsync(
                "x.nupkg", "", [], NullLogger.Instance,
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            NuGetPackageInstaller.InstallFromFileAsync(
                "x.nupkg", "anywhere", null!, NullLogger.Instance,
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            NuGetPackageInstaller.InstallFromFileAsync(
                "x.nupkg", "anywhere", [], null!,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallFromFileAsync_MissingFile_Throws()
    {
        using var dir = new TempDir();
        var bogus = Path.Combine(dir.Path, "absent.nupkg");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            NuGetPackageInstaller.InstallFromFileAsync(
                bogus, dir.Path, [], NullLogger.Instance,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallAsync_NullArguments_Throw()
    {
        // The online InstallAsync surface only validates inputs locally
        // before any network call — covering the guard branches without
        // hitting nuget.org.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NuGetPackageInstaller.InstallAsync(
                "", null, "anywhere", [], NullLogger.Instance,
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NuGetPackageInstaller.InstallAsync(
                "MyCo", null, "", [], NullLogger.Instance,
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            NuGetPackageInstaller.InstallAsync(
                "MyCo", null, "anywhere", null!, NullLogger.Instance,
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            NuGetPackageInstaller.InstallAsync(
                "MyCo", null, "anywhere", [], null!,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadAsync_NullArguments_Throw()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NuGetPackageInstaller.DownloadAsync(
                "", null, "anywhere", [], NullLogger.Instance,
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NuGetPackageInstaller.DownloadAsync(
                "MyCo", null, "", [], NullLogger.Instance,
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            NuGetPackageInstaller.DownloadAsync(
                "MyCo", null, "anywhere", null!, NullLogger.Instance,
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            NuGetPackageInstaller.DownloadAsync(
                "MyCo", null, "anywhere", [], null!,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_NullArguments_Throw()
    {
        // Both null + empty package id paths.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NuGetPackageInstaller.ResolveAsync(
                "", null, [], NullLogger.Instance,
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            NuGetPackageInstaller.ResolveAsync(
                "MyCo", null, null!, NullLogger.Instance,
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            NuGetPackageInstaller.ResolveAsync(
                "MyCo", null, [], null!,
                TestContext.Current.CancellationToken));
    }

    // ---- Pure helpers reached via reflection ----

    private static T InvokeStatic<T>(string name, params object?[] args)
    {
        var method = typeof(NuGetPackageInstaller).GetMethod(
            name, BindingFlags.NonPublic | BindingFlags.Static)!;
        return (T)method.Invoke(null, args)!;
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not-a-version", false)]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3-beta.1", true)]
    public void ParseVersion_Permutations(string? input, bool shouldParse)
    {
        var result = (NuGet.Versioning.NuGetVersion?)typeof(NuGetPackageInstaller)
            .GetMethod("ParseVersion", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [input]);
        Assert.Equal(shouldParse, result is not null);
    }

    [Theory]
    [InlineData("System.Text.Json", true)]
    [InlineData("Microsoft.Extensions.Logging", true)]
    [InlineData("NETStandard.Library", true)]
    [InlineData("ThirdParty.Random", false)]
    public void IsHostProvided_FrameworkPackages(string packageId, bool expected)
    {
        Assert.Equal(expected, InvokeStatic<bool>("IsHostProvided", packageId));
    }

    [Fact]
    public void IsHostProvided_KuestenlogikBowireUnknown_FallsThroughToFalse()
    {
        // A Kuestenlogik.Bowire.* id whose assembly isn't loaded in the
        // test process → the AppDomain scan returns false. The bound
        // here may flip if Bowire ever bakes the id into the test host;
        // the assertion stays robust because ".Made.Up" doesn't exist.
        Assert.False(InvokeStatic<bool>("IsHostProvided", "Kuestenlogik.Bowire.Made.Up"));
    }

    [Fact]
    public void IsHostProvided_KuestenlogikBowireLoaded_ReturnsTrue()
    {
        // The core Bowire assembly is referenced by the test project, so
        // it is in AppDomain.CurrentDomain.GetAssemblies(). Exercises
        // the AppDomain walk + the loaded-assembly hit branch.
        Assert.True(InvokeStatic<bool>("IsHostProvided", "Kuestenlogik.Bowire"));
    }

    [Fact]
    public void IsHostProvidedAssembly_DelegatesToIsHostProvided()
    {
        Assert.True(InvokeStatic<bool>("IsHostProvidedAssembly", "System.Text.Json"));
        Assert.False(InvokeStatic<bool>("IsHostProvidedAssembly", "ThirdParty.Random"));
    }

    // ---- Online-shaped paths driven against a local folder feed ----

    [Fact]
    public async Task InstallAsync_LocalFeed_SucceedsAndWritesLib()
    {
        using var dir = new TempDir();
        var feed = Path.Combine(dir.Path, "feed");
        Directory.CreateDirectory(feed);
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "alpha.test.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Alpha.Test", "1.0.0"),
            TestContext.Current.CancellationToken);

        var pluginDir = Path.Combine(dir.Path, "plugins");
        var result = await NuGetPackageInstaller.InstallAsync(
            "Alpha.Test", "1.0.0", pluginDir, [feed],
            NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal("Alpha.Test", result.PackageId);
        Assert.Equal("1.0.0", result.ResolvedVersion);
        Assert.True(File.Exists(Path.Combine(pluginDir, "Alpha.Test", "Stub.dll")));
    }

    [Fact]
    public async Task InstallAsync_LocalFeed_NoPin_PicksLatestStable()
    {
        // Two versions in the feed; ResolveVersionAsync picks the
        // highest stable when no pin is supplied. The 2.0.0 nupkg has a
        // different lib path so we can confirm it was the resolved
        // candidate.
        using var dir = new TempDir();
        var feed = Path.Combine(dir.Path, "feed");
        Directory.CreateDirectory(feed);
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "multi.test.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Multi.Test", "1.0.0"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "multi.test.2.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Multi.Test", "2.0.0"),
            TestContext.Current.CancellationToken);

        var pluginDir = Path.Combine(dir.Path, "plugins");
        var result = await NuGetPackageInstaller.InstallAsync(
            "Multi.Test", version: null, pluginDir, [feed],
            NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal("2.0.0", result.ResolvedVersion);
    }

    [Fact]
    public async Task InstallAsync_LocalFeed_PinExactVersion_HitsExactBranch()
    {
        // ResolveVersionAsync's exact-match branch — pin to a version
        // that does exist; result.ResolvedVersion equals the pin.
        using var dir = new TempDir();
        var feed = Path.Combine(dir.Path, "feed");
        Directory.CreateDirectory(feed);
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "pin.test.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Pin.Test", "1.0.0"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "pin.test.2.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Pin.Test", "2.0.0"),
            TestContext.Current.CancellationToken);

        var pluginDir = Path.Combine(dir.Path, "plugins");
        var result = await NuGetPackageInstaller.InstallAsync(
            "Pin.Test", "1.0.0", pluginDir, [feed],
            NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal("1.0.0", result.ResolvedVersion);
    }

    [Fact]
    public async Task InstallAsync_LocalFeed_PinUnavailableVersion_Throws()
    {
        // Pinned version not in the feed → the inner ResolveVersion
        // continues across repos, the outer "not found" diagnostic fires.
        using var dir = new TempDir();
        var feed = Path.Combine(dir.Path, "feed");
        Directory.CreateDirectory(feed);
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "ghost.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Ghost", "1.0.0"),
            TestContext.Current.CancellationToken);

        var pluginDir = Path.Combine(dir.Path, "plugins");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NuGetPackageInstaller.InstallAsync(
                "Ghost", "9.9.9", pluginDir, [feed],
                NullLogger.Instance, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallAsync_LocalFeed_WithDep_RecursesIntoTransitive()
    {
        // Root pkg depends on Dep.Test 1.0.0; both .nupkgs are in the
        // same flat feed, so the recursive walker lands the lib of both.
        using var dir = new TempDir();
        var feed = Path.Combine(dir.Path, "feed");
        Directory.CreateDirectory(feed);
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "rooted.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.WithDep("Rooted", "1.0.0", "Dep.Test", "1.0.0"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "dep.test.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Dep.Test", "1.0.0"),
            TestContext.Current.CancellationToken);

        var pluginDir = Path.Combine(dir.Path, "plugins");
        var result = await NuGetPackageInstaller.InstallAsync(
            "Rooted", "1.0.0", pluginDir, [feed],
            NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.PackagesResolved); // root + 1 dep
        // FilesWritten counts each lib copy regardless of basename
        // collision — the second copy overwrites on disk but the
        // counter still increments. Both copies happened, so the value
        // is 2 here, not 1.
        Assert.Equal(2, result.FilesWritten);
    }

    [Fact]
    public async Task ResolveAsync_LocalFeed_HitMatch()
    {
        // Pure metadata path — no extraction, no install. Returns the
        // ResolvedPackageInfo with id, version, source URL.
        using var dir = new TempDir();
        var feed = Path.Combine(dir.Path, "feed");
        Directory.CreateDirectory(feed);
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "lookup.1.2.3.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Lookup", "1.2.3"),
            TestContext.Current.CancellationToken);

        var info = await NuGetPackageInstaller.ResolveAsync(
            "Lookup", "1.2.3", [feed], NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.NotNull(info);
        Assert.Equal("Lookup", info!.PackageId);
        Assert.Equal("1.2.3", info.Version);
    }

    [Fact]
    public async Task ResolveAsync_LocalFeed_NoMatch_ReturnsNull()
    {
        using var dir = new TempDir();
        var feed = Path.Combine(dir.Path, "feed");
        Directory.CreateDirectory(feed);

        var info = await NuGetPackageInstaller.ResolveAsync(
            "Nope", null, [feed], NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Null(info);
    }

    [Fact]
    public async Task DownloadAsync_LocalFeed_WritesNupkgFile()
    {
        // Mirror image of InstallAsync — downloads raw .nupkg files
        // rather than extracting libs. Expected file name follows
        // NuGet's lowercased flat-feed convention.
        using var dir = new TempDir();
        var feed = Path.Combine(dir.Path, "feed");
        Directory.CreateDirectory(feed);
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "bundle.me.0.9.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Bundle.Me", "0.9.0"),
            TestContext.Current.CancellationToken);

        var output = Path.Combine(dir.Path, "out");
        var result = await NuGetPackageInstaller.DownloadAsync(
            "Bundle.Me", "0.9.0", output, [feed], NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal("Bundle.Me", result.RootPackageId);
        Assert.Single(result.Packages);
        Assert.Equal("Bundle.Me", result.Packages[0].PackageId);
        Assert.True(File.Exists(Path.Combine(output, "bundle.me.0.9.0.nupkg")));
    }

    [Fact]
    public async Task DownloadAsync_LocalFeed_WithDep_BundlesBoth()
    {
        using var dir = new TempDir();
        var feed = Path.Combine(dir.Path, "feed");
        Directory.CreateDirectory(feed);
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "rt.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.WithDep("Rt", "1.0.0", "Sub", "1.0.0"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "sub.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Sub", "1.0.0"),
            TestContext.Current.CancellationToken);

        var output = Path.Combine(dir.Path, "bundle");
        var result = await NuGetPackageInstaller.DownloadAsync(
            "Rt", "1.0.0", output, [feed], NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Packages.Count);
    }
}
