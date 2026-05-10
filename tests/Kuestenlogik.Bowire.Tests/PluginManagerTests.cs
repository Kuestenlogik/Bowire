// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for <see cref="PluginManager"/> that don't require a live NuGet
/// feed — directory resolution, list/uninstall on missing dirs, install
/// argument validation, and the LoadPlugins discovery walk over an
/// empty / stub plugin directory. The actual NuGet download path
/// (<c>InstallAsync</c> happy-path) needs network and is left for the
/// integration harness.
/// </summary>
public sealed class PluginManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _envBackup;

    public PluginManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-pm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _envBackup = Environment.GetEnvironmentVariable(PluginManager.PluginDirEnvVar);
        Environment.SetEnvironmentVariable(PluginManager.PluginDirEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PluginManager.PluginDirEnvVar, _envBackup);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ResolvePluginDir_Explicit_WinsOverEnvAndDefault()
    {
        Environment.SetEnvironmentVariable(PluginManager.PluginDirEnvVar, "/tmp/from-env");
        try
        {
            var resolved = PluginManager.ResolvePluginDir(_tempDir);
            Assert.Equal(Path.GetFullPath(_tempDir), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PluginManager.PluginDirEnvVar, null);
        }
    }

    [Fact]
    public void ResolvePluginDir_EnvVar_UsedWhenNoExplicitArg()
    {
        var target = Path.Combine(_tempDir, "env-dir");
        Environment.SetEnvironmentVariable(PluginManager.PluginDirEnvVar, target);
        try
        {
            var resolved = PluginManager.ResolvePluginDir(null);
            Assert.Equal(Path.GetFullPath(target), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PluginManager.PluginDirEnvVar, null);
        }
    }

    [Fact]
    public void ResolvePluginDir_WhitespaceExplicit_FallsThroughToEnv()
    {
        Environment.SetEnvironmentVariable(PluginManager.PluginDirEnvVar, _tempDir);
        try
        {
            var resolved = PluginManager.ResolvePluginDir("   ");
            Assert.Equal(Path.GetFullPath(_tempDir), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PluginManager.PluginDirEnvVar, null);
        }
    }

    [Fact]
    public void List_NonExistentDir_ReturnsZero()
    {
        var missing = Path.Combine(_tempDir, "nope");
        var rc = PluginManager.List(missing, verbose: false);
        Assert.Equal(0, rc);
    }

    [Fact]
    public void List_EmptyDir_ReturnsZero()
    {
        var rc = PluginManager.List(_tempDir, verbose: false);
        Assert.Equal(0, rc);
    }

    [Fact]
    public void List_DirWithStubPlugin_ReportsIt()
    {
        // Create a fake plugin with a plugin.json but no DLLs — exercises
        // the metadata-read + DLL-count branch without spinning up a
        // real load context.
        var pluginSub = Path.Combine(_tempDir, "stub-plugin");
        Directory.CreateDirectory(pluginSub);
        File.WriteAllText(Path.Combine(pluginSub, "plugin.json"),
            """
            {
              "packageId": "stub-plugin",
              "version": "1.0.0",
              "resolvedVersion": "1.0.0",
              "installedAt": "2026-01-01T00:00:00Z",
              "sources": ["https://api.nuget.org/v3/index.json"],
              "files": []
            }
            """);

        // Both verbose and non-verbose paths return 0 for a clean read.
        Assert.Equal(0, PluginManager.List(_tempDir, verbose: false));
        Assert.Equal(0, PluginManager.List(_tempDir, verbose: true));
    }

    [Fact]
    public void List_DirWithStubMissingMetadata_StillReturnsZero()
    {
        // No plugin.json — ReadPluginMetadata returns the empty-record
        // fallback, DisplayVersion shows "unknown", List still succeeds.
        Directory.CreateDirectory(Path.Combine(_tempDir, "no-meta"));
        Assert.Equal(0, PluginManager.List(_tempDir, verbose: true));
    }

    [Fact]
    public void List_DirWithMalformedMetadata_StillReturnsZero()
    {
        // Truncated plugin.json — ReadPluginMetadata catches the parse
        // error and returns the fallback, List should still return 0.
        var pluginSub = Path.Combine(_tempDir, "broken");
        Directory.CreateDirectory(pluginSub);
        File.WriteAllText(Path.Combine(pluginSub, "plugin.json"), "{ this is not json");

        Assert.Equal(0, PluginManager.List(_tempDir, verbose: true));
    }

    [Fact]
    public void Uninstall_EmptyPackageId_ReturnsUsageExit()
    {
        Assert.Equal(2, PluginManager.Uninstall("", _tempDir));
        Assert.Equal(2, PluginManager.Uninstall("   ", _tempDir));
    }

    [Fact]
    public void Uninstall_UnknownPackage_ReturnsOne()
    {
        Assert.Equal(1, PluginManager.Uninstall("not-installed", _tempDir));
    }

    [Fact]
    public void Uninstall_ExistingPlugin_DeletesAndReturnsZero()
    {
        var pluginSub = Path.Combine(_tempDir, "victim");
        Directory.CreateDirectory(pluginSub);
        File.WriteAllText(Path.Combine(pluginSub, "marker.txt"), "x");

        var rc = PluginManager.Uninstall("victim", _tempDir);
        Assert.Equal(0, rc);
        Assert.False(Directory.Exists(pluginSub));
    }

    [Fact]
    public async Task InstallAsync_EmptyPackageId_ReturnsUsageExit()
    {
        var rc = await PluginManager.InstallAsync(
            "", version: null, pluginDir: _tempDir, sources: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task InstallAsync_DuplicateInstall_ReturnsOne()
    {
        // Pre-create the destination so the "already installed" guard
        // fires before any network call is attempted.
        var pluginSub = Path.Combine(_tempDir, "dupe-pkg");
        Directory.CreateDirectory(pluginSub);

        var rc = await PluginManager.InstallAsync(
            "dupe-pkg", version: null, pluginDir: _tempDir, sources: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task InstallAsync_FromLocalFeed_WritesPluginJsonAndReturnsZero()
    {
        // Local-folder source containing a hand-rolled .nupkg → resolves
        // offline through NuGet.Protocol's flat-feed implementation,
        // then InstallAsync writes plugin.json + ResolvedVersion.
        var feedDir = Path.Combine(_tempDir, "local-feed");
        Directory.CreateDirectory(feedDir);
        var nupkgPath = Path.Combine(feedDir, "local.test.plugin.2.3.4.nupkg");
        await File.WriteAllBytesAsync(nupkgPath,
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Local.Test.Plugin", "2.3.4"),
            TestContext.Current.CancellationToken);

        var pluginsDir = Path.Combine(_tempDir, "plugins");
        var rc = await PluginManager.InstallAsync(
            "Local.Test.Plugin", version: "2.3.4",
            pluginDir: pluginsDir, sources: [feedDir],
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, rc);

        var meta = await File.ReadAllTextAsync(
            Path.Combine(pluginsDir, "Local.Test.Plugin", "plugin.json"),
            TestContext.Current.CancellationToken);
        Assert.Contains("\"resolvedVersion\": \"2.3.4\"", meta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAsync_FromLocalFeed_FailingResolution_ReturnsOne()
    {
        // Local feed exists but doesn't carry the requested package →
        // NuGetPackageInstaller throws "not found"; PluginManager
        // catches, deletes the partial dir, returns 1.
        var feedDir = Path.Combine(_tempDir, "empty-feed-2");
        Directory.CreateDirectory(feedDir);

        var pluginsDir = Path.Combine(_tempDir, "plugins-fail");
        var rc = await PluginManager.InstallAsync(
            "No.Such.Package", version: "1.0.0",
            pluginDir: pluginsDir, sources: [feedDir],
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, rc);
        // Cleanup branch ran — the per-package subdir got removed.
        Assert.False(Directory.Exists(Path.Combine(pluginsDir, "No.Such.Package")));
    }

    [Fact]
    public async Task DownloadAsync_FromLocalFeed_BundlesNupkg()
    {
        // Online download usually pulls .nupkg files from nuget.org; the
        // same flat-feed protocol works against a local folder, so we
        // can hit the success path without network. Output file gets
        // written with the lowercased-flat-feed name convention.
        var feedDir = Path.Combine(_tempDir, "feed-dl");
        Directory.CreateDirectory(feedDir);
        await File.WriteAllBytesAsync(
            Path.Combine(feedDir, "downloadable.test.1.5.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Downloadable.Test", "1.5.0"),
            TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(_tempDir, "bundle");
        var rc = await PluginManager.DownloadAsync(
            "Downloadable.Test", version: "1.5.0",
            outputDir: outputDir, sources: [feedDir],
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, rc);
        Assert.True(File.Exists(Path.Combine(outputDir, "downloadable.test.1.5.0.nupkg")));
    }

    [Fact]
    public async Task DownloadAsync_LocalFeedMissing_ReturnsOne()
    {
        // No package in the configured local feed → DownloadAsync
        // throws inside the recursive walker; the catch path returns 1.
        var feedDir = Path.Combine(_tempDir, "nothing");
        Directory.CreateDirectory(feedDir);

        var rc = await PluginManager.DownloadAsync(
            "Ghost.Pkg", version: "1.0.0",
            outputDir: Path.Combine(_tempDir, "drop"), sources: [feedDir],
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task InstallFromFileAsync_MissingPath_ReturnsOne()
    {
        var rc = await PluginManager.InstallFromFileAsync(
            Path.Combine(_tempDir, "does-not-exist.nupkg"),
            pluginDir: _tempDir, sources: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task InstallFromFileAsync_EmptyPath_ReturnsUsageExit()
    {
        var rc = await PluginManager.InstallFromFileAsync(
            "", pluginDir: _tempDir, sources: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task InstallFromFileAsync_CorruptNupkg_ReturnsErrorExit()
    {
        // Non-zip bytes at the path → the peek-archive branch catches
        // the read failure, prints "Failed to read", returns 1.
        var bogus = Path.Combine(_tempDir, "bogus.nupkg");
        await File.WriteAllTextAsync(bogus, "not a zip", TestContext.Current.CancellationToken);

        var rc = await PluginManager.InstallFromFileAsync(
            bogus, pluginDir: _tempDir, sources: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task InstallFromFileAsync_RealNupkg_InstallsAndWritesMetadata()
    {
        // Hand-rolled .nupkg with no deps → exercises the post-extract
        // success path: plugin.json gets written, exit 0.
        var nupkgBytes = NuGetPackageInstallerTests_NupkgFactory.NoDeps("Sample.Plug", "1.0.0");
        var nupkg = Path.Combine(_tempDir, "Sample.Plug.1.0.0.nupkg");
        await File.WriteAllBytesAsync(nupkg, nupkgBytes, TestContext.Current.CancellationToken);

        var rc = await PluginManager.InstallFromFileAsync(
            nupkg, pluginDir: _tempDir, sources: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, rc);

        var metaPath = Path.Combine(_tempDir, "Sample.Plug", "plugin.json");
        Assert.True(File.Exists(metaPath));
        var meta = await File.ReadAllTextAsync(metaPath, TestContext.Current.CancellationToken);
        Assert.Contains("Sample.Plug", meta, StringComparison.Ordinal);
        Assert.Contains("\"resolvedVersion\": \"1.0.0\"", meta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallFromFileAsync_DuplicateInstall_ReturnsOne()
    {
        // Pre-create the plugin subdir → "already installed" guard fires
        // after the peek-id step, returns 1 without extracting.
        Directory.CreateDirectory(Path.Combine(_tempDir, "Already.Installed"));
        var nupkg = Path.Combine(_tempDir, "Already.Installed.1.0.0.nupkg");
        await File.WriteAllBytesAsync(nupkg,
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Already.Installed", "1.0.0"),
            TestContext.Current.CancellationToken);

        var rc = await PluginManager.InstallFromFileAsync(
            nupkg, pluginDir: _tempDir, sources: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task InstallFromFileAsync_WithUnmetDep_PrintsWarningAndReturnsZero()
    {
        // Dep without a matching --source → the unmet-dep path on
        // PluginManager (lines around 219-229) renders the warning. Exit
        // is still 0 because the root install succeeded.
        var nupkgBytes = NuGetPackageInstallerTests_NupkgFactory.WithDep(
            "MyCo.Plug", "1.0.0", "Floating.Lib", "2.0.0");
        var nupkg = Path.Combine(_tempDir, "MyCo.Plug.1.0.0.nupkg");
        await File.WriteAllBytesAsync(nupkg, nupkgBytes, TestContext.Current.CancellationToken);

        var rc = await PluginManager.InstallFromFileAsync(
            nupkg, pluginDir: _tempDir, sources: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task DownloadAsync_EmptyPackageId_ReturnsUsageExit()
    {
        var rc = await PluginManager.DownloadAsync(
            "", version: null, outputDir: _tempDir, sources: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task DownloadAsync_EmptyOutputDir_ReturnsUsageExit()
    {
        var rc = await PluginManager.DownloadAsync(
            "MyCo.Plugin", version: null, outputDir: "", sources: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task UpdateAsync_EmptyPackageId_ReturnsUsageExit()
    {
        var rc = await PluginManager.UpdateAsync(
            "", targetVersion: null, pluginDir: _tempDir, sources: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task UpdateAsync_NotInstalled_ReturnsOne()
    {
        var rc = await PluginManager.UpdateAsync(
            "ghost", targetVersion: null, pluginDir: _tempDir, sources: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task UpdateAsync_InstalledButResolveFailsOnEmptyLocalFeed_ReturnsOne()
    {
        // Plugin exists on disk → past the "not installed" guard;
        // sources points at an empty local folder → ResolveAsync returns
        // null → the "Failed to resolve…" branch returns 1.
        var pluginSub = Path.Combine(_tempDir, "ghost-with-meta");
        Directory.CreateDirectory(pluginSub);
        await File.WriteAllTextAsync(Path.Combine(pluginSub, "plugin.json"),
            """
            {
              "packageId": "ghost-with-meta",
              "version": "1.0.0",
              "resolvedVersion": "1.0.0",
              "installedAt": "2026-01-01T00:00:00Z",
              "sources": []
            }
            """,
            TestContext.Current.CancellationToken);
        var emptyFeed = Path.Combine(_tempDir, "empty-feed");
        Directory.CreateDirectory(emptyFeed);

        var rc = await PluginManager.UpdateAsync(
            "ghost-with-meta", targetVersion: "1.0.0",
            pluginDir: _tempDir, sources: [emptyFeed],
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, rc);
        // The plugin dir should still be there — Update only deletes
        // after a successful resolve.
        Assert.True(Directory.Exists(pluginSub));
    }

    [Fact]
    public async Task UpdateAsync_AlreadyAtTargetVersion_PrintsNoOpAndReturnsZero()
    {
        // Plugin already installed at 1.0.0; pin --version 1.0.0; the
        // resolve happens against a local folder feed that contains the
        // same .nupkg → resolved.Version matches installed.Version →
        // "already at" branch returns 0.
        var pluginSub = Path.Combine(_tempDir, "Pin.Lock");
        Directory.CreateDirectory(pluginSub);
        await File.WriteAllTextAsync(Path.Combine(pluginSub, "plugin.json"),
            """
            {
              "packageId": "Pin.Lock",
              "version": "1.0.0",
              "resolvedVersion": "1.0.0",
              "installedAt": "2026-01-01T00:00:00Z",
              "sources": []
            }
            """,
            TestContext.Current.CancellationToken);

        var feedDir = Path.Combine(_tempDir, "feed");
        Directory.CreateDirectory(feedDir);
        // Flat-feed convention: lower-case id.version.nupkg
        var nupkgPath = Path.Combine(feedDir, "pin.lock.1.0.0.nupkg");
        await File.WriteAllBytesAsync(nupkgPath,
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Pin.Lock", "1.0.0"),
            TestContext.Current.CancellationToken);

        var rc = await PluginManager.UpdateAsync(
            "Pin.Lock", targetVersion: "1.0.0",
            pluginDir: _tempDir, sources: [feedDir],
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task UpdateAllAsync_DirWithStubPlugins_ProcessesEach()
    {
        // Two plugin subdirs → UpdateAllAsync iterates both, each calls
        // UpdateAsync → ResolveAsync → returns null (empty local feed)
        // → returns 1. UpdateAllAsync surfaces the first non-zero.
        Directory.CreateDirectory(Path.Combine(_tempDir, "plugin-a"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "plugin-b"));
        var emptyFeed = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyFeed);

        var rc = await PluginManager.UpdateAllAsync(
            pluginDir: _tempDir, sources: [emptyFeed],
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task UpdateAllAsync_EmptyPluginDir_ReturnsZero()
    {
        var rc = await PluginManager.UpdateAllAsync(
            pluginDir: Path.Combine(_tempDir, "missing"), sources: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task UpdateAllAsync_EmptyExistingDir_ReturnsZero()
    {
        var rc = await PluginManager.UpdateAllAsync(
            pluginDir: _tempDir, sources: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, rc);
    }

    [Fact]
    public void Inspect_EmptyPackageId_ReturnsUsageExit()
    {
        Assert.Equal(2, PluginManager.Inspect("", _tempDir));
    }

    [Fact]
    public void Inspect_NotInstalled_ReturnsOne()
    {
        Assert.Equal(1, PluginManager.Inspect("not-here", _tempDir));
    }

    [Fact]
    public void ShowHelp_PrintsAndReturnsZero()
    {
        // Capture stdout to confirm a non-trivial help blob is emitted.
        var prev = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var rc = PluginManager.ShowHelp();
            Assert.Equal(0, rc);
            var output = sw.ToString();
            Assert.Contains("bowire plugin", output, StringComparison.Ordinal);
            Assert.Contains("install", output, StringComparison.Ordinal);
            Assert.Contains(PluginManager.PluginDirEnvVar, output, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(prev);
        }
    }

    [Fact]
    public void LoadPlugins_NonExistentDir_NoOp()
    {
        // Should silently return — exercised here so the early-out
        // branch is covered without any side-effects.
        PluginManager.LoadPlugins(Path.Combine(_tempDir, "nope"));
    }

    [Fact]
    public void LoadPlugins_EmptyDir_NoOp()
    {
        PluginManager.LoadPlugins(_tempDir);
    }

    [Fact]
    public void LoadPlugins_DirWithEmptyPluginSubdir_DoesNotThrow()
    {
        // A subdirectory with no DLLs creates a load context but loads
        // nothing — exercises the for-each-DLL branch with zero entries.
        Directory.CreateDirectory(Path.Combine(_tempDir, "stub"));
        PluginManager.LoadPlugins(_tempDir);
        // EnumeratePluginServices over an arbitrary contract returns
        // an empty list when no plugin contributes one — confirms the
        // no-op behaviour without depending on test ordering.
        var emitters = PluginManager.EnumeratePluginServices<DummyContract>();
        Assert.NotNull(emitters);
    }

    [Fact]
    public void Inspect_StubPluginWithRealAssembly_LoadsAndEnumerates()
    {
        // Drop the test host's own Kuestenlogik.Bowire.dll into a stub
        // plugin dir. BowirePluginHost.Load picks it up via
        // LoadFromAssemblyPath; Inspect's `loadedAssemblies` list ends
        // up non-empty so the foreach-and-print branch runs, plus
        // FindImplementationsOf walks every type — exercises the real
        // discovery path without needing a third-party plugin .nupkg.
        var pluginSub = Path.Combine(_tempDir, "stub-real");
        Directory.CreateDirectory(pluginSub);
        var hostBowire = typeof(IBowireProtocol).Assembly.Location;
        Assert.NotEqual(string.Empty, hostBowire);
        File.Copy(hostBowire, Path.Combine(pluginSub, "Kuestenlogik.Bowire.dll"));
        File.WriteAllText(Path.Combine(pluginSub, "plugin.json"),
            """
            {
              "packageId": "stub-real",
              "version": "0.0.1",
              "resolvedVersion": "0.0.1",
              "installedAt": "2026-01-01T00:00:00Z",
              "sources": []
            }
            """);

        var rc = PluginManager.Inspect("stub-real", _tempDir);
        Assert.Equal(0, rc);
    }

    [Fact]
    public void Inspect_StubPluginNoDlls_ReturnsZero()
    {
        // Inspect on a real subdirectory with a plugin.json + zero DLLs
        // exercises the metadata read, ALC creation, and the "no Bowire
        // contracts found" diagnostic without needing a real plugin
        // assembly. We don't redirect Console.Out — that's process-wide
        // and races with parallel tests in other classes.
        var pluginSub = Path.Combine(_tempDir, "stub-inspect");
        Directory.CreateDirectory(pluginSub);
        File.WriteAllText(Path.Combine(pluginSub, "plugin.json"),
            """
            {
              "packageId": "stub-inspect",
              "version": "0.1.0",
              "resolvedVersion": "0.1.0",
              "installedAt": "2026-01-01T00:00:00Z",
              "sources": ["https://api.nuget.org/v3/index.json"],
              "files": []
            }
            """);

        var rc = PluginManager.Inspect("stub-inspect", _tempDir);
        Assert.Equal(0, rc);
    }

    [Fact]
    public void EnumeratePluginServices_NoPluginsLoaded_ReturnsEmpty()
    {
        // No LoadPlugins() call before this test → s_pluginContexts may be
        // non-empty from prior tests, but the contract type is private to
        // this file so nothing in any plugin ALC implements it.
        var hits = PluginManager.EnumeratePluginServices<DummyContract>();
        Assert.NotNull(hits);
        Assert.Empty(hits);
    }

    private sealed class DummyContract { /* contract probe — never instantiated */ }
}
