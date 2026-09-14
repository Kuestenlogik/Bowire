// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Plugins;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// #666 — an install that left runtime dependencies unresolved. The
/// warning tells the operator to run the install again with
/// <c>--source</c>; the "already installed" guard must let that through,
/// and <c>plugin list</c> must not present the half-install as a normal
/// one in the meantime. Offline throughout: the dependency lives in a
/// local folder feed.
/// </summary>
public sealed class PluginInstallIncompleteTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _envBackup;

    public PluginInstallIncompleteTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("bowire-pm-incomplete-").FullName;
        _envBackup = Environment.GetEnvironmentVariable(BowirePluginOptions.EnvVarName);
        Environment.SetEnvironmentVariable(BowirePluginOptions.EnvVarName, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BowirePluginOptions.EnvVarName, _envBackup);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private async Task<(string Nupkg, string Feed)> ArrangeRootWithDepAsync()
    {
        var feed = SafePath.Combine(_tempDir, "feed");
        Directory.CreateDirectory(feed);
        var nupkg = SafePath.Combine(_tempDir, "needy.root.1.0.0.nupkg");
        await File.WriteAllBytesAsync(nupkg,
            NuGetPackageInstallerTests_NupkgFactory.WithDep("Needy.Root", "1.0.0", "Needed.Dep", "1.0.0"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(SafePath.Combine(feed, "needed.dep.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Needed.Dep", "1.0.0"),
            TestContext.Current.CancellationToken);
        return (nupkg, feed);
    }

    [Fact]
    public async Task InstallWithoutSource_RecordsTheUnmetDependency_AndListSaysIncomplete()
    {
        var (nupkg, _) = await ArrangeRootWithDepAsync();
        var plugins = SafePath.Combine(_tempDir, "plugins");

        using var install = new StringWriter();
        var rc = await PluginManager.InstallFromFileAsync(
            nupkg, pluginDir: plugins, sources: [], stdout: install, stderr: install,
            ct: TestContext.Current.CancellationToken);

        // Installed-with-warning is exit 0: the package itself is on disk.
        Assert.Equal(0, rc);
        Assert.Contains("runtime dependencies that weren't installed", install.ToString(), StringComparison.Ordinal);
        Assert.Contains("Needed.Dep", install.ToString(), StringComparison.Ordinal);
        // The remedy names what actually works — no detour through uninstall.
        Assert.Contains("replaced, not refused", install.ToString(), StringComparison.Ordinal);

        var meta = await File.ReadAllTextAsync(SafePath.Combine(plugins, "Needy.Root", "plugin.json"),
            TestContext.Current.CancellationToken);
        Assert.Contains("\"unmetDependencies\"", meta, StringComparison.Ordinal);
        Assert.Contains("Needed.Dep", meta, StringComparison.Ordinal);

        using var list = new StringWriter();
        Assert.Equal(0, PluginManager.List(plugins, verbose: false, stdout: list, stderr: list));
        Assert.Contains("INCOMPLETE", list.ToString(), StringComparison.Ordinal);
        Assert.Contains("Needed.Dep", list.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAgainWithSource_ReplacesTheIncompleteInstall_AndCompletesIt()
    {
        var (nupkg, feed) = await ArrangeRootWithDepAsync();
        var plugins = SafePath.Combine(_tempDir, "plugins");

        Assert.Equal(0, await PluginManager.InstallFromFileAsync(
            nupkg, pluginDir: plugins, sources: [], stdout: TextWriter.Null, stderr: TextWriter.Null,
            ct: TestContext.Current.CancellationToken));

        // The advice, verbatim: the same install, now with --source.
        using var second = new StringWriter();
        var rc = await PluginManager.InstallFromFileAsync(
            nupkg, pluginDir: plugins, sources: [feed], stdout: second, stderr: second,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        Assert.DoesNotContain("already installed", second.ToString(), StringComparison.Ordinal);
        Assert.Contains("Replacing the incomplete install of Needy.Root", second.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("weren't installed", second.ToString(), StringComparison.Ordinal);

        var meta = await File.ReadAllTextAsync(SafePath.Combine(plugins, "Needy.Root", "plugin.json"),
            TestContext.Current.CancellationToken);
        Assert.Contains("\"unmetDependencies\": []", meta, StringComparison.Ordinal);

        using var list = new StringWriter();
        Assert.Equal(0, PluginManager.List(plugins, verbose: false, stdout: list, stderr: list));
        Assert.DoesNotContain("INCOMPLETE", list.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallOverACompleteInstall_IsStillRefused()
    {
        var plugins = SafePath.Combine(_tempDir, "plugins");
        var nupkg = SafePath.Combine(_tempDir, "whole.1.0.0.nupkg");
        await File.WriteAllBytesAsync(nupkg,
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Whole", "1.0.0"),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, await PluginManager.InstallFromFileAsync(
            nupkg, pluginDir: plugins, stdout: TextWriter.Null, stderr: TextWriter.Null,
            ct: TestContext.Current.CancellationToken));

        using var again = new StringWriter();
        var rc = await PluginManager.InstallFromFileAsync(
            nupkg, pluginDir: plugins, stdout: again, stderr: again,
            ct: TestContext.Current.CancellationToken);

        // A complete install keeps the guard: nothing is replaced by accident.
        Assert.Equal(1, rc);
        Assert.Contains("already installed", again.ToString(), StringComparison.Ordinal);
    }
}
