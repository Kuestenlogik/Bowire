// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Plugins;
using NuGet.Common;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Covers the transitive-dependency branch of
/// <see cref="NuGetPackageInstaller.InstallFromFileAsync"/> — when
/// <c>sources</c> is non-empty the installer recurses into the
/// dependencies via the same local-feed pipeline the online path uses.
/// Sibling to <see cref="NuGetPackageInstallerTests"/>, which only
/// covers the unmet-dep + no-source variants.
/// </summary>
public sealed class NuGetPackageInstallerTransitiveTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("bowire-nupkg-trans-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task InstallFromFileAsync_WithDepResolvedFromLocalFeed_PullsBothPackages()
    {
        // Two-package fixture: Root depends on Dep. The local feed has
        // both .nupkg files. InstallFromFileAsync extracts Root, then
        // recurses into Dep via the configured local-folder source.
        var feed = Path.Combine(_tempDir, "feed");
        Directory.CreateDirectory(feed);

        // Flat-feed convention: lowercased-id.version.nupkg.
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "root.plugin.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.WithDep(
                "Root.Plugin", "1.0.0", "Dep.Plugin", "1.0.0"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "dep.plugin.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Dep.Plugin", "1.0.0"),
            TestContext.Current.CancellationToken);

        var rootNupkg = Path.Combine(feed, "root.plugin.1.0.0.nupkg");
        var pluginDir = Path.Combine(_tempDir, "plugins");
        var result = await NuGetPackageInstaller.InstallFromFileAsync(
            rootNupkg, pluginDir, sources: [feed], NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal("Root.Plugin", result.PackageId);
        Assert.Empty(result.UnmetDependencies);
        // PackagesResolved counts root + every transitive dep visited
        // through the recursive walk.
        Assert.True(result.PackagesResolved >= 2);
    }

    [Fact]
    public async Task InstallFromFileAsync_WithHostProvidedDepInFeed_SkipsInstall()
    {
        // Root depends on Microsoft.Extensions.Logging which the host
        // already provides. The recursive walker's IsHostProvided
        // shortcut returns early — exercises the "skip host-provided"
        // branch inside InstallRecursiveAsync. Even though pluginDeps
        // filters host-provided deps out before recursing, the test
        // covers the shape via an explicit pin.
        var feed = Path.Combine(_tempDir, "host-feed");
        Directory.CreateDirectory(feed);
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "host.root.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.WithDep(
                "Host.Root", "1.0.0", "Microsoft.Extensions.Logging", "9.0.0"),
            TestContext.Current.CancellationToken);

        var rootNupkg = Path.Combine(feed, "host.root.1.0.0.nupkg");
        var pluginDir = Path.Combine(_tempDir, "plugins-host");
        var result = await NuGetPackageInstaller.InstallFromFileAsync(
            rootNupkg, pluginDir, sources: [feed], NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal("Host.Root", result.PackageId);
        Assert.Empty(result.UnmetDependencies);
        // Only the root counts — the host-provided dep was filtered out.
        Assert.Equal(1, result.PackagesResolved);
    }

    [Fact]
    public async Task InstallFromFileAsync_WithDepMissingFromFeed_BubblesError()
    {
        // Root references a dep that the configured feed doesn't carry.
        // ResolveVersionAsync returns (null, null) → InstallRecursiveAsync
        // throws → the outer InstallFromFileAsync surfaces the exception
        // (the CLI wrapper in PluginManager catches it). Here we assert
        // directly on the throw.
        var feed = Path.Combine(_tempDir, "partial");
        Directory.CreateDirectory(feed);
        await File.WriteAllBytesAsync(
            Path.Combine(feed, "root.plugin.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.WithDep(
                "Root.Plugin", "1.0.0", "Ghost.Lib", "9.9.9"),
            TestContext.Current.CancellationToken);

        var rootNupkg = Path.Combine(feed, "root.plugin.1.0.0.nupkg");
        var pluginDir = Path.Combine(_tempDir, "plugins-fail");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NuGetPackageInstaller.InstallFromFileAsync(
                rootNupkg, pluginDir, sources: [feed], NullLogger.Instance,
                TestContext.Current.CancellationToken));
    }
}
