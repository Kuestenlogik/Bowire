// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Interceptor;
using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Coverage for the v2.2 rail-IA refactor — the unified
/// <see cref="BowireInterceptRailContribution"/> descriptor that
/// replaces the previous Mocks + Traffic rails (plus the already-hidden
/// Intercepted + Proxy descriptors, which were dropped in the same
/// refactor).
/// </summary>
/// <remarks>
/// The four-sub-tab behaviour (Captured / Live overrides / Mock servers
/// / Settings) lives in the JS bundle; these tests cover the static
/// descriptor contract that the workbench shell consumes and the
/// registry-discovery hookup.
/// </remarks>
public class BowireInterceptRailContributionTests
{
    [Fact]
    public void Descriptor_Has_Stable_InterceptId()
    {
        // The boot migration in prologue.js rewrites localStorage
        // entries 'mocks' / 'traffic' / 'proxy' / 'intercepted' to
        // 'intercept' — if this id changes, that migration breaks
        // silently.
        var rail = new BowireInterceptRailContribution();
        Assert.Equal("intercept", rail.Id);
    }

    [Fact]
    public void Descriptor_Visible_In_Rail_Strip()
    {
        // Sanity: HideFromRail must be false so the rail icon actually
        // renders. The legacy Proxy + Intercepted descriptors used to
        // ship HideFromRail=true during their deprecation window; that
        // window expired in v2.2 and the types were deleted.
        IBowireRailContribution rail = new BowireInterceptRailContribution();
        Assert.False(rail.HideFromRail);
        Assert.False(rail.AlwaysOn);
        Assert.True(rail.DefaultEnabled);
    }

    [Fact]
    public void Descriptor_Sidebar_Kind_Is_Intercept()
    {
        // render-sidebar.js dispatches on this string; changing it
        // requires a matching arm in the switch / case there.
        var rail = new BowireInterceptRailContribution();
        Assert.Equal("intercept", rail.SidebarKind);
    }

    [Fact]
    public void Descriptor_Group_Is_Quality()
    {
        // Inherits the Traffic rail's group so the rail-strip ordering
        // doesn't shift slot relative to the other quality-group rails.
        var rail = new BowireInterceptRailContribution();
        Assert.Equal("quality", rail.Group);
    }

    [Fact]
    public void Descriptor_Sort_Index_Inherits_Traffic_Slot()
    {
        // Operator muscle memory — 950 was Traffic's slot; the merged
        // rail's icon lands in exactly the same rail-strip position.
        var rail = new BowireInterceptRailContribution();
        Assert.Equal(950, rail.SortIndex);
    }

    [Fact]
    public void Descriptor_Discovered_By_Registry()
    {
        // Force the assembly into the AppDomain so the reflection scan
        // picks it up (same pattern as the always-on rails in
        // BowireRailRegistryTests.Discover_Picks_Up_BuiltIn_Rails_*).
        _ = new BowireInterceptRailContribution().Id;

        var registry = BowireRailRegistry.Discover();
        var rail = registry.GetById("intercept");
        Assert.NotNull(rail);
        Assert.Equal("Intercept", rail!.DisplayName);
    }

    [Fact]
    public void Legacy_Rail_Descriptors_Are_Gone()
    {
        // R2 of the v2.2 refactor: the Mocks + Proxy + Intercepted +
        // Environments descriptors are deleted (no longer in any loaded
        // Bowire assembly). The boot migration rewrites their persisted
        // ids on first paint, so existing installs don't see an
        // unresolved rail; this assertion guards against a regression
        // that quietly re-adds one of them.
        _ = new BowireInterceptRailContribution().Id;

        var registry = BowireRailRegistry.Discover();
        Assert.Null(registry.GetById("mocks"));
        Assert.Null(registry.GetById("traffic"));
        Assert.Null(registry.GetById("proxy"));
        Assert.Null(registry.GetById("intercepted"));
        Assert.Null(registry.GetById("environments"));
    }
}
