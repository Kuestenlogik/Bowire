// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Plugins;
using NuGet.Common;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Argument-validation tests for <see cref="NuGetPackageInstaller"/>.
/// The actual download paths hit nuget.org (or a local feed), so the
/// happy path stays in integration tests; here we just lock the public
/// constants and the explicit ArgumentException guards.
/// </summary>
public sealed class NuGetPackageInstallerTests : IDisposable
{
    private readonly string _tempDir;

    public NuGetPackageInstallerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-nuget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void DefaultSource_IsNuGetOrgV3()
    {
        // The CLI documents this as a stable contract — any change would
        // ripple through every plugin.json file (the resolver writes the
        // effective source list there).
        Assert.Equal("https://api.nuget.org/v3/index.json", NuGetPackageInstaller.DefaultSource);
    }

    [Fact]
    public async Task InstallAsync_NullPackageId_Throws()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            NuGetPackageInstaller.InstallAsync(
                packageId: null!, version: null,
                pluginDir: _tempDir, sources: [],
                logger: NullLogger.Instance,
                ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallAsync_EmptyPackageId_Throws()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            NuGetPackageInstaller.InstallAsync(
                packageId: "", version: null,
                pluginDir: _tempDir, sources: [],
                logger: NullLogger.Instance,
                ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallAsync_NullPluginDir_Throws()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            NuGetPackageInstaller.InstallAsync(
                packageId: "MyCo.Plugin", version: null,
                pluginDir: null!, sources: [],
                logger: NullLogger.Instance,
                ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallAsync_NullSources_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            NuGetPackageInstaller.InstallAsync(
                packageId: "MyCo.Plugin", version: null,
                pluginDir: _tempDir, sources: null!,
                logger: NullLogger.Instance,
                ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallAsync_NullLogger_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            NuGetPackageInstaller.InstallAsync(
                packageId: "MyCo.Plugin", version: null,
                pluginDir: _tempDir, sources: [],
                logger: null!,
                ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallFromFileAsync_MissingFile_ThrowsFileNotFound()
    {
        var path = Path.Combine(_tempDir, "missing.nupkg");
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            NuGetPackageInstaller.InstallFromFileAsync(
                nupkgPath: path,
                pluginDir: _tempDir, sources: [],
                logger: NullLogger.Instance,
                ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallFromFileAsync_NullPath_Throws()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            NuGetPackageInstaller.InstallFromFileAsync(
                nupkgPath: null!,
                pluginDir: _tempDir, sources: [],
                logger: NullLogger.Instance,
                ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallFromFileAsync_EmptyPath_Throws()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            NuGetPackageInstaller.InstallFromFileAsync(
                nupkgPath: "",
                pluginDir: _tempDir, sources: [],
                logger: NullLogger.Instance,
                ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadAsync_NullPackageId_Throws()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            NuGetPackageInstaller.DownloadAsync(
                packageId: null!, version: null,
                outputDir: _tempDir, sources: [],
                logger: NullLogger.Instance,
                ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadAsync_EmptyOutputDir_Throws()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            NuGetPackageInstaller.DownloadAsync(
                packageId: "MyCo.Plugin", version: null,
                outputDir: "", sources: [],
                logger: NullLogger.Instance,
                ct: TestContext.Current.CancellationToken));
    }
}
