// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Plugins;
using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Where the disabled-plugins list lives, and who it speaks for (#284 Phase D).
/// </summary>
/// <remarks>
/// <para>
/// Disabling a plugin re-runs <c>BowireProtocolRegistry.Discover</c> against
/// the merged set and swaps the registry every session reads — so its effect
/// has always been process-wide. The file, however, was written under
/// <c>&lt;user-store&gt;/</c>, which on a multi-tenant install gave each
/// identity a private copy of a shared decision: the last person to touch it
/// decided for everybody, and the file that explained why was in somebody
/// else's directory.
/// </para>
/// <para>
/// These tests are the statement that the two now agree. Not "each identity
/// gets their own list" — that is a different feature, for a different file,
/// and conflating them is what produced the defect.
/// </para>
/// </remarks>
[Collection("BowireStorageRoot")]
public sealed class BowireDisabledPluginsScopeTests : IDisposable
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-disabled-scope-" + Guid.NewGuid().ToString("N"));

    // Unique per test class run: the store caches in a process-global static,
    // so a shared id would let one test's Disable no-op the next one's.
    private readonly string _pluginId = "Scope.Plugin." + Guid.NewGuid().ToString("N")[..8];

    private readonly IBowirePathResolver _previousPaths = BowirePaths.Current;
    private readonly IBowireUserStore _previousUsers = BowireUserContext.Current;

    public BowireDisabledPluginsScopeTests()
    {
        Directory.CreateDirectory(_root);
        BowirePaths.Current = new BowirePathResolver(
            name => name == BowirePathResolver.DataDirVariable ? _root : null,
            () => _root);
        BowireDisabledPluginsStore.ResetForTests();
    }

    public void Dispose()
    {
        BowirePaths.Current = _previousPaths;
        BowireUserContext.Current = _previousUsers;
        BowireDisabledPluginsStore.ResetForTests();
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void TheListLandsInTheStorageRootRatherThanAnIdentitysSlot()
    {
        // The multi-tenant layout: each identity has a slot under users/<slug>/.
        // A decision that takes effect for the whole process must not be filed
        // inside one of them.
        BowireUserContext.Current = new ScopedBowireUserStore(_root, Ada);

        BowireDisabledPluginsStore.Disable(_pluginId);

        Assert.True(File.Exists(Path.Combine(_root, "disabled-plugins.json")));

        var slots = Path.Combine(_root, "users");
        string[] strays = Directory.Exists(slots)
            ? Directory.GetFiles(slots, "disabled-plugins.json", SearchOption.AllDirectories)
            : [];
        Assert.Empty(strays);
    }

    [Fact]
    public void WhatOneIdentityDisablesReadsBackTheSameForAnother()
    {
        // The behaviour the old layout could not deliver: Ada unloads a
        // protocol, the process unloads it, and Grace's workbench is looking
        // at the same process. Now the file says so too.
        BowireUserContext.Current = new ScopedBowireUserStore(_root, Ada);
        BowireDisabledPluginsStore.Disable(_pluginId);

        BowireUserContext.Current = new ScopedBowireUserStore(_root, Grace);
        BowireDisabledPluginsStore.ResetForTests();

        Assert.True(BowireDisabledPluginsStore.IsDisabled(_pluginId));
    }

    [Fact]
    public void SingleUserModeResolvesTheSamePathItAlwaysDid()
    {
        // Why no migration ships with this: in the flat layout a person's
        // state sits directly in the storage root, so the user store and the
        // Data scope were already naming one file. A laptop notices nothing.
        BowireUserContext.Current = new DefaultBowireUserStore(_root);

        var viaUserStore = BowireUserContext.GetUserPath("disabled-plugins.json");
        var viaStorageRoot = BowirePaths.Resolve(BowireStorageScope.Data, "disabled-plugins.json");

        Assert.Equal(Path.GetFullPath(viaUserStore), Path.GetFullPath(viaStorageRoot));
    }

    [Fact]
    public void TheBaselineFromConfigurationStillWins()
    {
        // MergeWith is what the lifecycle endpoint re-discovers against. A
        // plugin pinned off in appsettings.json must not come back because
        // somebody enabled it in the UI — that pin is the operator's, and
        // Bowire has no writer for their file.
        BowireUserContext.Current = new DefaultBowireUserStore(_root);
        BowireDisabledPluginsStore.Enable(_pluginId);

        var merged = BowireDisabledPluginsStore.MergeWith([_pluginId]);

        Assert.Contains(_pluginId, merged, StringComparer.OrdinalIgnoreCase);
    }
}
