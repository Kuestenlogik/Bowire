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

    // Both are process-global, and the tier cases below swap the resolver.
    // Restoring it matters more than the override: a suite that runs after
    // this one would otherwise resolve every storage question into a temp
    // directory that no longer exists.
    private readonly IBowirePathResolver _previousPaths = BowirePaths.Current;

    public void Dispose()
    {
        BowirePluginRoot.Apply(null);
        BowirePaths.Current = _previousPaths;
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

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

    // ---- two tiers (#28 Phase D) ----

    /// <summary>Materialise a package directory under <paramref name="root"/>.</summary>
    private static void AddPackage(string root, string packageId)
        => Directory.CreateDirectory(Path.Combine(root, packageId));

    /// <summary>
    /// Redirect the two scopes at two temp trees, so "user" and "machine" are
    /// genuinely different directories. The real resolver reads %ProgramData%
    /// for the machine root, which a test has no business writing to.
    /// </summary>
    private (string User, string Machine) SplitTiers()
    {
        var user = Path.Combine(_dir, "user-tier");
        var machine = Path.Combine(_dir, "machine-tier");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(machine);
        BowirePaths.Current = new TwoTierPathResolver(user, machine);
        return (Path.Combine(user, "plugins"), Path.Combine(machine, "plugins"));
    }

    [Fact]
    public void With_No_Machine_Tier_Present_Only_The_User_Packages_Are_Found()
    {
        // The common install, and the behaviour that must not change: no
        // %ProgramData%\Bowire\plugins exists, so nothing new appears.
        var (user, _) = SplitTiers();
        AddPackage(user, "Some.Plugin");

        var found = BowirePluginRoot.EnumeratePackages().ToList();

        Assert.Equal(["Some.Plugin"], found.Select(p => p.PackageId));
        Assert.All(found, p => Assert.Equal(BowirePluginTier.User, p.Tier));
    }

    [Fact]
    public void A_Machine_Wide_Package_Is_Found_Without_Being_Installed_By_The_User()
    {
        // The point of the phase: an administrator provisions once, every
        // account on the host sees it.
        var (_, machine) = SplitTiers();
        AddPackage(machine, "Admin.Provisioned");

        var found = BowirePluginRoot.EnumeratePackages().ToList();

        var one = Assert.Single(found);
        Assert.Equal("Admin.Provisioned", one.PackageId);
        Assert.Equal(BowirePluginTier.Machine, one.Tier);
    }

    [Fact]
    public void Both_Tiers_Are_Searched()
    {
        var (user, machine) = SplitTiers();
        AddPackage(user, "Mine");
        AddPackage(machine, "Theirs");

        var found = BowirePluginRoot.EnumeratePackages().ToList();

        Assert.Equal(2, found.Count);
        Assert.Contains(found, p => p.PackageId == "Mine" && p.Tier == BowirePluginTier.User);
        Assert.Contains(found, p => p.PackageId == "Theirs" && p.Tier == BowirePluginTier.Machine);
    }

    [Fact]
    public void The_User_Copy_Shadows_A_Machine_Wide_One_Of_The_Same_Package()
    {
        // What makes it an overlay rather than a second list: someone tries a
        // newer build of a plugin without an administrator changing what
        // everybody else gets. Yielding both would have the loader register
        // one protocol twice.
        var (user, machine) = SplitTiers();
        AddPackage(user, "Shared.Plugin");
        AddPackage(machine, "Shared.Plugin");

        var found = BowirePluginRoot.EnumeratePackages().ToList();

        var one = Assert.Single(found);
        Assert.Equal(BowirePluginTier.User, one.Tier);
        Assert.StartsWith(Path.GetFullPath(user), Path.GetFullPath(one.Directory), StringComparison.Ordinal);
    }

    [Fact]
    public void Shadowing_Ignores_Case_Because_Package_Ids_Do()
    {
        // NuGet ids are case-insensitive, and the two tiers can easily have
        // been written by different tools.
        var (user, machine) = SplitTiers();
        AddPackage(user, "Shared.Plugin");
        AddPackage(machine, "shared.plugin");

        Assert.Single(BowirePluginRoot.EnumeratePackages());
    }

    [Fact]
    public void An_Explicitly_Configured_Directory_Is_The_Only_One_Searched()
    {
        // `--plugin-dir /tmp/isolated` is an operator saying which plugins
        // are in play. Adding a machine tier they did not ask for would make
        // an isolated run stop being isolated.
        var (_, machine) = SplitTiers();
        AddPackage(machine, "Admin.Provisioned");
        var isolated = Path.Combine(_dir, "isolated");
        AddPackage(isolated, "Only.This");

        BowirePluginRoot.Apply(isolated);

        Assert.Equal(["Only.This"], BowirePluginRoot.EnumeratePackages().Select(p => p.PackageId));
    }

    [Fact]
    public void A_Caller_That_Resolved_Its_Own_User_Root_Gets_The_Same_Overlay()
    {
        // The Tool resolves the directory through BowirePluginOptions, whose
        // precedence chain Core does not model, so the loader passes what it
        // resolved rather than trusting an earlier Apply.
        var (_, machine) = SplitTiers();
        AddPackage(machine, "Shared.Plugin");
        var ownRoot = Path.Combine(_dir, "own");
        AddPackage(ownRoot, "Shared.Plugin");
        AddPackage(ownRoot, "Mine");

        var found = BowirePluginRoot
            .EnumeratePackagesUnder(ownRoot, includeMachineTier: true).ToList();

        Assert.Equal(2, found.Count);
        Assert.All(found, p => Assert.Equal(BowirePluginTier.User, p.Tier));
    }

    [Fact]
    public void A_Caller_Can_Refuse_The_Machine_Tier()
    {
        var (_, machine) = SplitTiers();
        AddPackage(machine, "Admin.Provisioned");
        var ownRoot = Path.Combine(_dir, "own");
        AddPackage(ownRoot, "Mine");

        var found = BowirePluginRoot
            .EnumeratePackagesUnder(ownRoot, includeMachineTier: false).ToList();

        Assert.Equal(["Mine"], found.Select(p => p.PackageId));
    }

    [Fact]
    public void One_Directory_Serving_Both_Tiers_Lists_Each_Package_Once()
    {
        // BOWIRE_DATA_DIR redirects every scope at one tree — the shape every
        // test fixture uses — and a naive two-pass walk would double it.
        var shared = Path.Combine(_dir, "shared");
        Directory.CreateDirectory(shared);
        BowirePaths.Current = new TwoTierPathResolver(shared, shared);
        AddPackage(Path.Combine(shared, "plugins"), "Some.Plugin");

        Assert.Single(BowirePluginRoot.EnumeratePackages());
    }

    [Fact]
    public void A_Missing_Root_Is_Not_An_Error()
    {
        // Neither directory has to exist: a fresh install has no plugins, and
        // most hosts have no machine tier at all.
        BowirePaths.Current = new TwoTierPathResolver(
            Path.Combine(_dir, "nope-user"), Path.Combine(_dir, "nope-machine"));

        Assert.Empty(BowirePluginRoot.EnumeratePackages());
    }
}

/// <summary>
/// A resolver that puts <see cref="BowireStorageScope.Data"/> and
/// <see cref="BowireStorageScope.Machine"/> in two different trees.
/// </summary>
/// <remarks>
/// The real resolver answers the machine scope with <c>%ProgramData%\Bowire</c>
/// or <c>/var/lib/bowire</c>, and the fixture override every other suite uses
/// (<c>BOWIRE_DATA_DIR</c>) deliberately collapses every scope onto one
/// directory — which is exactly the distinction these tests are about. Top
/// level rather than nested: CA1034 is an error in this repository.
/// </remarks>
internal sealed class TwoTierPathResolver(string dataRoot, string machineRoot) : IBowirePathResolver
{
    public string Root(BowireStorageScope scope)
        => scope == BowireStorageScope.Machine ? machineRoot : dataRoot;

    public string Resolve(BowireStorageScope scope, params string[] segments)
        => segments is { Length: > 0 }
            ? Path.Combine(Root(scope), Path.Combine(segments))
            : Root(scope);
}
