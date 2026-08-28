// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.Plugins;
using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// What happens when someone tries to uninstall a plugin that is not theirs
/// to remove (#28 Phase D).
/// </summary>
/// <remarks>
/// <para>
/// A machine-wide plugin is loaded, listed, and visibly doing its job. Before
/// the tier existed, the uninstall path looked in one directory and said "not
/// installed" about anything it did not find there — which, for this plugin,
/// is the one thing it certainly is not. That message sends the reader
/// hunting for a typo in a package id that is spelled correctly.
/// </para>
/// <para>
/// Shares <c>CwdSerialised</c> with the rest of the plugin-root suite: both
/// swap <see cref="BowirePaths.Current"/>, which is process-global.
/// </para>
/// </remarks>
[Collection("CwdSerialised")]
public sealed class MachineTierUninstallTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bowire-tier-uninstall-" + Guid.NewGuid().ToString("N"));
    private readonly IBowirePathResolver _previousPaths = BowirePaths.Current;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    private readonly string _userRoot;
    private readonly string _machineRoot;

    public MachineTierUninstallTests()
    {
        var user = Path.Combine(_dir, "user");
        var machine = Path.Combine(_dir, "machine");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(machine);
        BowirePaths.Current = new TwoTierPathResolver(user, machine);
        _userRoot = Path.Combine(user, "plugins");
        _machineRoot = Path.Combine(machine, "plugins");
        Directory.CreateDirectory(_userRoot);
        Directory.CreateDirectory(_machineRoot);
    }

    public void Dispose()
    {
        BowirePaths.Current = _previousPaths;
        _out.Dispose();
        _err.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void AddPackage(string root, string packageId)
        => Directory.CreateDirectory(Path.Combine(root, packageId));

    private int Uninstall(string packageId)
        => PluginManager.Uninstall(packageId, _userRoot, _out, _err);

    [Fact]
    public void A_Machine_Wide_Plugin_Is_Not_Reported_As_Missing()
    {
        // The regression this exists for: "not installed" about a plugin the
        // workbench is listing and the host has loaded.
        AddPackage(_machineRoot, "Admin.Provisioned");

        var exit = Uninstall("Admin.Provisioned");

        Assert.NotEqual(0, exit);
        Assert.DoesNotContain("not installed", _out.ToString() + _err.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void It_Says_Whose_Call_The_Removal_Is()
    {
        // The reader cannot act on this themselves. What they can do is know
        // that, and know who can.
        AddPackage(_machineRoot, "Admin.Provisioned");

        Uninstall("Admin.Provisioned");

        var said = _err.ToString();
        Assert.Contains("machine-wide", said, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("administrator", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void It_Names_The_Directory_And_The_Command_That_Would_Work()
    {
        // An administrator reading this over someone's shoulder should not
        // have to work out the incantation.
        AddPackage(_machineRoot, "Admin.Provisioned");

        Uninstall("Admin.Provisioned");

        var said = _err.ToString();
        Assert.Contains(_machineRoot, said, StringComparison.Ordinal);
        Assert.Contains("--plugin-dir", said, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Machine_Copy_Is_Left_On_Disk()
    {
        // Refusing has to mean refusing. The failure worth guarding against
        // is a "helpful" fallback that removes it for every account.
        AddPackage(_machineRoot, "Admin.Provisioned");

        Uninstall("Admin.Provisioned");

        Assert.True(Directory.Exists(Path.Combine(_machineRoot, "Admin.Provisioned")));
    }

    [Fact]
    public void A_Plugin_In_Neither_Tier_Is_Still_Just_Missing()
    {
        // The new branch must not swallow the ordinary case: a typo should
        // read as a typo, not as a permissions lecture.
        var exit = Uninstall("Never.Installed");

        Assert.NotEqual(0, exit);
        Assert.Contains("not installed", _out.ToString() + _err.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("administrator", _out.ToString() + _err.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_Users_Own_Copy_Is_Removed_Even_When_A_Machine_Copy_Exists()
    {
        // The overlay case. Uninstalling here means "stop shadowing it", and
        // the machine-wide one takes over again — so the user copy must go
        // and the machine copy must stay.
        AddPackage(_userRoot, "Shared.Plugin");
        AddPackage(_machineRoot, "Shared.Plugin");

        var exit = Uninstall("Shared.Plugin");

        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(Path.Combine(_userRoot, "Shared.Plugin")));
        Assert.True(Directory.Exists(Path.Combine(_machineRoot, "Shared.Plugin")));
    }
}
