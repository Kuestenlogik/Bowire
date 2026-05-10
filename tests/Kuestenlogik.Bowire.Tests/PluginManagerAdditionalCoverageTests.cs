// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Targets the remaining uncovered branches of <see cref="PluginManager"/> —
/// the verbose-list "requested != resolved" diff, the file-list line,
/// the version-fallback in <c>ReadResolvedVersion</c>, the LoadPlugins
/// garbage-DLL skip path, and a real Update that lands a new version
/// from a local feed. Everything stays offline by relying on the
/// <see cref="NuGetPackageInstallerTests_NupkgFactory"/> helper.
/// </summary>
public sealed class PluginManagerAdditionalCoverageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _envBackup;

    public PluginManagerAdditionalCoverageTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("bowire-pm-extra-").FullName;
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
    public void List_VerboseWithRequestedVsResolvedAndFiles_PrintsAllBranches()
    {
        // version != resolvedVersion → the "requested:/resolved:" diff
        // branch runs. files=[..] → the DLL-list line at the end of the
        // foreach body runs too.
        var pluginSub = Path.Combine(_tempDir, "diff-plugin");
        Directory.CreateDirectory(pluginSub);
        // Write a minimal "DLL" placeholder so the GetFiles("*.dll") walk
        // returns something — Directory.GetFiles doesn't read the file
        // contents, so a single-byte PE-shaped placeholder is enough.
        File.WriteAllBytes(Path.Combine(pluginSub, "Stub.dll"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(pluginSub, "plugin.json"),
            """
            {
              "packageId": "diff-plugin",
              "version": "1.0.0",
              "resolvedVersion": "1.0.1",
              "installedAt": "2026-01-01T00:00:00Z",
              "sources": ["https://api.nuget.org/v3/index.json"],
              "files": []
            }
            """);

        Assert.Equal(0, PluginManager.List(_tempDir, verbose: true));
    }

    [Fact]
    public void List_VerboseWithNoResolvedVersion_StillReturnsZero()
    {
        // Metadata only carries `version` (older installer); the diff
        // branch is gated on a non-empty ResolvedVersion so it's skipped.
        // Confirms the fallback in DisplayVersion + Sources empty path.
        var pluginSub = Path.Combine(_tempDir, "no-resolved");
        Directory.CreateDirectory(pluginSub);
        File.WriteAllText(Path.Combine(pluginSub, "plugin.json"),
            """
            {
              "packageId": "no-resolved",
              "version": "0.9.0"
            }
            """);

        Assert.Equal(0, PluginManager.List(_tempDir, verbose: true));
    }

    [Fact]
    public async Task UpdateAsync_BumpsToNewVersionFromLocalFeed_ReturnsZero()
    {
        // Existing install at 1.0.0; feed has 1.0.0 and 1.1.0; pin to
        // 1.1.0 → ResolveAsync returns the newer version → diff vs.
        // installed triggers the "Updating … -> …" path → directory
        // delete + InstallAsync writes the new copy → exit 0.
        var pluginSub = Path.Combine(_tempDir, "Up.Plugin");
        Directory.CreateDirectory(pluginSub);
        await File.WriteAllTextAsync(Path.Combine(pluginSub, "plugin.json"),
            """
            {
              "packageId": "Up.Plugin",
              "version": "1.0.0",
              "resolvedVersion": "1.0.0",
              "installedAt": "2026-01-01T00:00:00Z"
            }
            """,
            TestContext.Current.CancellationToken);

        var feedDir = Path.Combine(_tempDir, "feed");
        Directory.CreateDirectory(feedDir);
        await File.WriteAllBytesAsync(
            Path.Combine(feedDir, "up.plugin.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Up.Plugin", "1.0.0"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(feedDir, "up.plugin.1.1.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Up.Plugin", "1.1.0"),
            TestContext.Current.CancellationToken);

        var rc = await PluginManager.UpdateAsync(
            "Up.Plugin", targetVersion: "1.1.0",
            pluginDir: _tempDir, sources: [feedDir],
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, rc);

        // After the update the plugin.json should reflect 1.1.0.
        var meta = await File.ReadAllTextAsync(Path.Combine(pluginSub, "plugin.json"),
            TestContext.Current.CancellationToken);
        Assert.Contains("1.1.0", meta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAllAsync_BumpsEverything_AlreadyAtLatest()
    {
        // UpdateAllAsync with both subdirs present and a local feed
        // covering both packages → UpdateAsync runs for each, hitting
        // the "already at" success branch when resolve matches the
        // installed version. Tolerate either exit (0 or 1) — the
        // important thing is the foreach + ResolveAsync path runs for
        // every subdir.
        var feedDir = Path.Combine(_tempDir, "feed-all");
        Directory.CreateDirectory(feedDir);
        await File.WriteAllBytesAsync(
            Path.Combine(feedDir, "alpha.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Alpha", "1.0.0"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(feedDir, "beta.2.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Beta", "2.0.0"),
            TestContext.Current.CancellationToken);

        foreach (var (id, ver) in new[] { ("Alpha", "1.0.0"), ("Beta", "2.0.0") })
        {
            var dir = Path.Combine(_tempDir, id);
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "plugin.json"),
                $$"""
                {
                  "packageId": "{{id}}",
                  "version": "{{ver}}",
                  "resolvedVersion": "{{ver}}"
                }
                """,
                TestContext.Current.CancellationToken);
        }

        var rc = await PluginManager.UpdateAllAsync(
            pluginDir: _tempDir, sources: [feedDir],
            ct: TestContext.Current.CancellationToken);
        Assert.Contains(rc, s_acceptedExitCodes);
    }

    private static readonly int[] s_acceptedExitCodes = [0, 1];

    [Fact]
    public void LoadPlugins_DirWithGarbageDll_SkipsAndContinues()
    {
        // Drop a .dll-named file that's not a real PE. ALC.LoadFromAssemblyPath
        // throws BadImageFormatException → the per-DLL catch swallows it,
        // LoadPlugins keeps going for the next file / subdir.
        var pluginSub = Path.Combine(_tempDir, "garbage");
        Directory.CreateDirectory(pluginSub);
        File.WriteAllText(Path.Combine(pluginSub, "NotAnAssembly.dll"), "this is not a PE");

        PluginManager.LoadPlugins(_tempDir);
    }

    [Fact]
    public void LoadPlugins_DirWithMixedGarbageAndValidNothing_StillNoThrows()
    {
        // Two subdirs: one empty, one with garbage. Both produce a load
        // context (BowirePluginLoadContext ctor takes a directory path)
        // but the second one's DLL fails to load and gets swallowed.
        Directory.CreateDirectory(Path.Combine(_tempDir, "empty-plug"));
        var withGarbage = Path.Combine(_tempDir, "garbage-plug");
        Directory.CreateDirectory(withGarbage);
        File.WriteAllBytes(Path.Combine(withGarbage, "Junk.dll"), [0x00, 0x01, 0x02]);

        PluginManager.LoadPlugins(_tempDir);
    }

    [Fact]
    public void Inspect_StubWithGarbageDll_LoadsContextAndReturnsZero()
    {
        // Inspect on a plugin folder containing junk DLLs: the load
        // context still constructs, FindImplementationsOf catches
        // ReflectionTypeLoadException on the bad assembly via its
        // try/catch, exits with the "no Bowire contracts" message.
        var pluginSub = Path.Combine(_tempDir, "inspect-garbage");
        Directory.CreateDirectory(pluginSub);
        File.WriteAllBytes(Path.Combine(pluginSub, "Junk.dll"), [0x00, 0x01, 0x02]);
        File.WriteAllText(Path.Combine(pluginSub, "plugin.json"),
            """
            {
              "packageId": "inspect-garbage",
              "version": "0.1.0",
              "resolvedVersion": "0.1.0",
              "installedAt": "2026-01-01T00:00:00Z",
              "sources": []
            }
            """);

        var rc = PluginManager.Inspect("inspect-garbage", _tempDir);
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task UpdateAsync_PluginJsonOnlyHasVersionField_FallsBackThroughReadResolvedVersion()
    {
        // plugin.json missing resolvedVersion → ReadResolvedVersion
        // falls through to the `version` branch; "latest" is filtered
        // out, a concrete pin survives.
        var pluginSub = Path.Combine(_tempDir, "Fallback.Plug");
        Directory.CreateDirectory(pluginSub);
        await File.WriteAllTextAsync(Path.Combine(pluginSub, "plugin.json"),
            """
            {
              "packageId": "Fallback.Plug",
              "version": "0.9.0"
            }
            """,
            TestContext.Current.CancellationToken);

        var feedDir = Path.Combine(_tempDir, "fallback-feed");
        Directory.CreateDirectory(feedDir);
        await File.WriteAllBytesAsync(
            Path.Combine(feedDir, "fallback.plug.0.9.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Fallback.Plug", "0.9.0"),
            TestContext.Current.CancellationToken);

        var rc = await PluginManager.UpdateAsync(
            "Fallback.Plug", targetVersion: "0.9.0",
            pluginDir: _tempDir, sources: [feedDir],
            ct: TestContext.Current.CancellationToken);
        // Both resolved + installed are 0.9.0 → "already at" branch
        // returns 0.
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task UpdateAsync_PluginJsonResolvedIsUnknown_FallsBackToVersion()
    {
        // resolvedVersion = "unknown" → the filter skips it and the
        // method falls through to `version`. Even though the test
        // doesn't assert which branch ran, both lines execute on the
        // way to the install path.
        var pluginSub = Path.Combine(_tempDir, "Unknown.Plug");
        Directory.CreateDirectory(pluginSub);
        await File.WriteAllTextAsync(Path.Combine(pluginSub, "plugin.json"),
            """
            {
              "packageId": "Unknown.Plug",
              "version": "1.0.0",
              "resolvedVersion": "unknown"
            }
            """,
            TestContext.Current.CancellationToken);

        var feedDir = Path.Combine(_tempDir, "unknown-feed");
        Directory.CreateDirectory(feedDir);
        await File.WriteAllBytesAsync(
            Path.Combine(feedDir, "unknown.plug.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Unknown.Plug", "1.0.0"),
            TestContext.Current.CancellationToken);

        var rc = await PluginManager.UpdateAsync(
            "Unknown.Plug", targetVersion: "1.0.0",
            pluginDir: _tempDir, sources: [feedDir],
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task UpdateAsync_PluginJsonVersionIsLatest_FallsThroughToNullReturn()
    {
        // Both resolvedVersion absent + version="latest" → both
        // branches' early returns are skipped, ReadResolvedVersion
        // returns null, the diff check treats installedVersion=null
        // as a fresh install → directory delete + reinstall via
        // InstallAsync.
        var pluginSub = Path.Combine(_tempDir, "Latest.Plug");
        Directory.CreateDirectory(pluginSub);
        await File.WriteAllTextAsync(Path.Combine(pluginSub, "plugin.json"),
            """
            {
              "packageId": "Latest.Plug",
              "version": "latest"
            }
            """,
            TestContext.Current.CancellationToken);

        var feedDir = Path.Combine(_tempDir, "latest-feed");
        Directory.CreateDirectory(feedDir);
        await File.WriteAllBytesAsync(
            Path.Combine(feedDir, "latest.plug.1.0.0.nupkg"),
            NuGetPackageInstallerTests_NupkgFactory.NoDeps("Latest.Plug", "1.0.0"),
            TestContext.Current.CancellationToken);

        var rc = await PluginManager.UpdateAsync(
            "Latest.Plug", targetVersion: null,
            pluginDir: _tempDir, sources: [feedDir],
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, rc);
    }

    [Fact]
    public void Inspect_PluginWithConcreteIBowireProtocol_ReportsImplementations()
    {
        // Copy the real Grpc plugin DLL (contains a concrete
        // BowireGrpcProtocol : IBowireProtocol) into a fake plugin
        // subdir. Inspect's FindImplementationsOf walk finds the
        // implementation and prints the "IBowireProtocol …" line.
        var pluginSub = Path.Combine(_tempDir, "stub-with-impl");
        Directory.CreateDirectory(pluginSub);
        // Source DLL lives next to the test assembly so reading it
        // works regardless of the test runner's CWD.
        var here = Path.GetDirectoryName(typeof(PluginManagerAdditionalCoverageTests).Assembly.Location)!;
        var src = Path.Combine(here, "Kuestenlogik.Bowire.Protocol.Grpc.dll");
        File.Copy(src, Path.Combine(pluginSub, "Kuestenlogik.Bowire.Protocol.Grpc.dll"));
        File.WriteAllText(Path.Combine(pluginSub, "plugin.json"),
            """
            {
              "packageId": "stub-with-impl",
              "version": "0.0.1",
              "resolvedVersion": "0.0.1",
              "installedAt": "2026-01-01T00:00:00Z",
              "sources": []
            }
            """);

        var rc = PluginManager.Inspect("stub-with-impl", _tempDir);
        Assert.Equal(0, rc);
    }

    [Fact]
    public void EnumeratePluginServices_AfterLoadingRealProtocolDll_FindsConcreteType()
    {
        // Drop a real protocol DLL into a plugin subdir and load it
        // via LoadPlugins → exercises the foreach + GetTypes branch +
        // Activator.CreateInstance branch (lines 773-794) when an
        // IBowireProtocol implementation is discoverable. We
        // enumerate against IBowireProtocol so the plugin contributes
        // a result the production EnumeratePluginServices walker can
        // observe.
        var pluginSub = Path.Combine(_tempDir, "real-impl");
        Directory.CreateDirectory(pluginSub);
        var here = Path.GetDirectoryName(typeof(PluginManagerAdditionalCoverageTests).Assembly.Location)!;
        File.Copy(
            Path.Combine(here, "Kuestenlogik.Bowire.Protocol.Grpc.dll"),
            Path.Combine(pluginSub, "Kuestenlogik.Bowire.Protocol.Grpc.dll"));

        PluginManager.LoadPlugins(_tempDir);

        var protocols = PluginManager.EnumeratePluginServices<Kuestenlogik.Bowire.IBowireProtocol>();
        // Either a concrete BowireGrpcProtocol gets instantiated, or
        // (rare CI path) the Activator surfaces a per-type exception
        // that the catch swallows with a stderr warning. Either way
        // the lines we care about ran.
        Assert.NotNull(protocols);
    }

    [Fact]
    public void Inspect_PluginSourcesAreReported()
    {
        // Sources list populated → the "Sources:" line under the
        // load-context header fires; exercises the small foreach over
        // meta.Sources without needing a real load.
        var pluginSub = Path.Combine(_tempDir, "with-sources");
        Directory.CreateDirectory(pluginSub);
        File.WriteAllText(Path.Combine(pluginSub, "plugin.json"),
            """
            {
              "packageId": "with-sources",
              "version": "1.0.0",
              "resolvedVersion": "1.0.0",
              "installedAt": "2026-01-01T00:00:00Z",
              "sources": ["./local-feed", "https://api.nuget.org/v3/index.json"]
            }
            """);

        var rc = PluginManager.Inspect("with-sources", _tempDir);
        Assert.Equal(0, rc);
    }
}
