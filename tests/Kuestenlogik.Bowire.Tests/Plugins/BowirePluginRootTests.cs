// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Plugins;
using Kuestenlogik.Bowire.Plugins.Sidecar;
using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// Where Bowire looks for installed plugins, and whether every reader agrees
/// (#549).
/// </summary>
/// <remarks>
/// <para>
/// The bug this pins down was not a wrong path — it was four right paths that
/// disagreed. A host started with <c>--plugin-dir X</c> installed into X and
/// then listed the storage default, so the plugin appeared in the workbench
/// while the host kept loading from somewhere else.
/// </para>
/// <para>
/// In <c>CwdSerialised</c> rather than a collection of its own: the override
/// is process-global, and <c>BowireProtocolRegistryEdgeCasesTests</c> reads
/// <see cref="SidecarPluginDiscovery.DefaultPluginRoot"/> from that
/// collection. Sharing it makes the two serial instead of racing over one
/// static. <see cref="Dispose"/> restores the default either way.
/// </para>
/// </remarks>
[Collection("CwdSerialised")]
public sealed class BowirePluginRootTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bowire-plugindir-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BowirePluginRoot.Apply(null);

    // ---- the resolution itself ----

    [Fact]
    public void With_Nothing_Configured_It_Is_The_Storage_Resolver_Default()
    {
        // The default has to keep coming from BowirePaths rather than a
        // second copy of the path logic — that is what #616 consolidated,
        // and re-introducing a literal here would undo it.
        BowirePluginRoot.Apply(null);

        Assert.Equal(
            BowirePaths.Resolve(BowireStorageScope.Data, "plugins"),
            BowirePluginRoot.Current);
        Assert.False(BowirePluginRoot.IsConfigured);
    }

    [Fact]
    public void A_Configured_Directory_Wins()
    {
        BowirePluginRoot.Apply(_dir);

        Assert.Equal(Path.GetFullPath(_dir), BowirePluginRoot.Current);
        Assert.True(BowirePluginRoot.IsConfigured);
    }

    [Fact]
    public void A_Relative_Directory_Is_Rooted_When_It_Is_Applied()
    {
        // Asserted as "rooted", not against a specific absolute path: the
        // point is that the working directory stops mattering the moment
        // Apply runs. The update check fires on a timer long after start-up
        // and has no reason to still be where the operator launched from —
        // resolving late would walk a different tree than the installer
        // wrote to.
        BowirePluginRoot.Apply("plugins-relative");

        Assert.True(Path.IsPathRooted(BowirePluginRoot.Current),
            $"expected an absolute path, got '{BowirePluginRoot.Current}'");
        Assert.EndsWith("plugins-relative", BowirePluginRoot.Current, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_Useful_Clears_The_Override(string? value)
    {
        // Hosts call Apply unconditionally with whatever Bowire:PluginDir
        // yielded, so an unset key has to mean "use the default" rather
        // than "install into the working directory".
        BowirePluginRoot.Apply(_dir);

        BowirePluginRoot.Apply(value);

        Assert.Equal(
            BowirePaths.Resolve(BowireStorageScope.Data, "plugins"),
            BowirePluginRoot.Current);
        Assert.False(BowirePluginRoot.IsConfigured);
    }

    // ---- the readers that used to disagree ----

    [Fact]
    public void Sidecar_Discovery_Follows_The_Configured_Directory()
    {
        // Sidecars live beside .NET plugins under the same root, so one
        // left behind in the default directory is a protocol the host
        // silently does not have.
        BowirePluginRoot.Apply(_dir);

        Assert.Equal(Path.GetFullPath(_dir), SidecarPluginDiscovery.DefaultPluginRoot);
    }

    // ---- the child process ----

    [Fact]
    public void The_Install_Shell_Out_Passes_The_Directory_Explicitly()
    {
        // The acceptance criterion of #549. The child inherits the parent's
        // environment, so BOWIRE_PLUGIN_DIR carried over by accident while
        // --plugin-dir and an appsettings.json entry did not survive the
        // process boundary at all.
        var argv = BowirePluginEndpoints.BuildPluginArgv(
            "install", "Kuestenlogik.Bowire.Protocol.Amqp", version: null,
            prerelease: false, pluginDir: _dir);

        var flag = argv.IndexOf("--plugin-dir");
        Assert.True(flag >= 0, $"--plugin-dir missing from: {string.Join(' ', argv)}");
        Assert.Equal(_dir, argv[flag + 1]);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("update")]
    [InlineData("uninstall")]
    public void The_Directory_Travels_On_Every_Verb(string verb)
    {
        // Uninstalling from the workbench has to remove the plugin the
        // workbench listed — the one in the configured directory.
        var argv = BowirePluginEndpoints.BuildPluginArgv(
            verb, "Kuestenlogik.Bowire.Protocol.Amqp", version: null,
            prerelease: false, pluginDir: _dir);

        Assert.Contains("--plugin-dir", argv);
        Assert.Equal(_dir, argv[argv.IndexOf("--plugin-dir") + 1]);
    }

    [Fact]
    public void The_Existing_Arguments_Are_Untouched()
    {
        // The flag is appended, so nothing above it may shift: verb and
        // package id stay in the slots the CLI parses positionally.
        var argv = BowirePluginEndpoints.BuildPluginArgv(
            "install", "Some.Package", version: "1.2.3", prerelease: true, pluginDir: _dir);

        Assert.Equal("plugin", argv[0]);
        Assert.Equal("install", argv[1]);
        Assert.Equal("Some.Package", argv[2]);
        Assert.Equal("1.2.3", argv[argv.IndexOf("--version") + 1]);
        Assert.Contains("--prerelease", argv);
    }

    [Fact]
    public void Prerelease_Stays_Off_A_Verb_That_Has_No_Such_Flag()
    {
        // `bowire plugin uninstall --prerelease` is a parse error, and the
        // request body carries the flag regardless of verb.
        var argv = BowirePluginEndpoints.BuildPluginArgv(
            "uninstall", "Some.Package", version: null, prerelease: true, pluginDir: _dir);

        Assert.DoesNotContain("--prerelease", argv);
    }
}
