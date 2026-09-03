// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// `BOWIRE_DATA_DIR` reaches everything, or it is not an isolation mechanism
/// (#643).
/// </summary>
/// <remarks>
/// <para>
/// It used to reach half. `BowirePathResolver` read the variable, so the
/// plugin directory moved; `BowireStorageRoot` did not, so the user store
/// stayed at the real profile and every workspace-scoped artifact went with
/// it — collections, recordings, flows, plugin settings. A run that believed
/// it was isolated wrote into the developer's own storage and left
/// directories behind. I hit that by hand: two workspaces invented for a
/// check turned up in my home directory rather than the scratch root I had
/// named.
/// </para>
/// <para>
/// The assertion that matters is not "each store lands in the right place" —
/// that is a list somebody has to keep adding to. It is that the two
/// resolvers give the same answer, which is a property that cannot rot as
/// stores are added.
/// </para>
/// </remarks>
[Collection("BowireStorageRoot")]
public sealed class BowireDataDirOverrideTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "bowire-datadir-" + Guid.NewGuid().ToString("N"));

    private readonly string? _previousEnv = Environment.GetEnvironmentVariable("BOWIRE_DATA_DIR");
    private readonly IBowirePathResolver _previousPaths = BowirePaths.Current;
    private readonly IBowireUserStore _previousUsers = BowireUserContext.Current;

    public BowireDataDirOverrideTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BOWIRE_DATA_DIR", _previousEnv);
        BowirePaths.Current = _previousPaths;
        BowireUserContext.Current = _previousUsers;
        try { Directory.Delete(_scratch, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void TheTwoResolversAgreeWhenTheVariableIsSet()
    {
        Environment.SetEnvironmentVariable("BOWIRE_DATA_DIR", _scratch);
        BowirePaths.Current = new BowirePathResolver();

        // What the plugin directory follows…
        var viaPaths = BowirePaths.Root(BowireStorageScope.Data);
        // …and what everything workspace-scoped follows.
        var viaStorageRoot = BowireStorageRoot.Resolve();

        Assert.Equal(Path.GetFullPath(_scratch), Path.GetFullPath(viaPaths));
        Assert.Equal(Path.GetFullPath(viaPaths), Path.GetFullPath(viaStorageRoot));
    }

    [Fact]
    public void AWorkspacesArtifactsLandUnderTheOverride()
    {
        Environment.SetEnvironmentVariable("BOWIRE_DATA_DIR", _scratch);
        BowirePaths.Current = new BowirePathResolver();
        BowireUserContext.Current = new DefaultBowireUserStore(BowireStorageRoot.Resolve());

        var flows = BowireUserContext.GetWorkspacePath("team-a", null, "flows.json");

        Assert.StartsWith(Path.GetFullPath(_scratch), Path.GetFullPath(flows), StringComparison.Ordinal);
    }

    [Fact]
    public void AGitNativeWorkspaceStillWinsOverTheOverride()
    {
        // The override says where *Bowire's* storage goes. A workspace the
        // operator pointed at a checkout is not Bowire's storage — it is
        // theirs, and it must keep landing in the repository so it travels
        // with a clone.
        Environment.SetEnvironmentVariable("BOWIRE_DATA_DIR", _scratch);
        BowirePaths.Current = new BowirePathResolver();
        BowireUserContext.Current = new DefaultBowireUserStore(BowireStorageRoot.Resolve());

        var checkout = Path.Combine(_scratch, "..", "checkout-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(checkout);
        try
        {
            var flows = BowireUserContext.GetWorkspacePath("team-a", checkout, "flows.json");
            Assert.StartsWith(Path.GetFullPath(checkout), Path.GetFullPath(flows), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(checkout, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void WithTheVariableUnsetNothingMoves()
    {
        // The ordinary install, and the half of this that must not change.
        Environment.SetEnvironmentVariable("BOWIRE_DATA_DIR", null);
        BowirePaths.Current = new BowirePathResolver();

        Assert.Null(BowirePathResolver.DataDirOverride());
        Assert.Equal(
            Path.GetFullPath(BowireStorageRoot.Resolve()),
            Path.GetFullPath(BowirePaths.Root(BowireStorageScope.Data)));
    }

    [Fact]
    public void BlankIsUnset()
    {
        // An exported-but-empty variable is what a shell script produces when
        // its own variable was never set. Treating it as a root would resolve
        // storage to the working directory.
        Environment.SetEnvironmentVariable("BOWIRE_DATA_DIR", "   ");

        Assert.Null(BowirePathResolver.DataDirOverride());
    }
}
