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
