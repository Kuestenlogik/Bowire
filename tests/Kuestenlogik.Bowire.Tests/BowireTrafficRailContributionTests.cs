// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

#pragma warning disable CS0618 // Legacy descriptors are intentionally referenced here
using Kuestenlogik.Bowire.Interceptor;
using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Coverage for #315 — the unified <see cref="BowireTrafficRailContribution"/>
/// descriptor that replaces the per-deployment Proxy + Intercepted rails.
/// </summary>
/// <remarks>
/// The header / Settings sub-tab adaptation lives in the JS bundle (driven
/// by <c>__BOWIRE_CONFIG__.embeddedMode</c>); these tests cover the static
/// descriptor contract that the workbench shell consumes and the
/// registry-discovery hookup.
/// </remarks>
public class BowireTrafficRailContributionTests
{
    [Fact]
    public void Descriptor_Has_Stable_TrafficId()
    {
        // The boot migration in prologue.js rewrites localStorage entries
        // 'proxy' / 'intercepted' to 'traffic' — if this id changes, that
        // migration breaks silently.
        var rail = new BowireTrafficRailContribution();
        Assert.Equal("traffic", rail.Id);
    }

    [Fact]
    public void Descriptor_Visible_In_Rail_Strip()
    {
        // Sanity: HideFromRail must be false so the rail icon actually
        // renders. The legacy Proxy + Intercepted descriptors are
        // HideFromRail = true during the deprecation window so the
        // strip doesn't carry three sibling icons. These flags live as
        // default-interface members, so the assertions go through the
        // IBowireRailContribution surface.
        IBowireRailContribution rail = new BowireTrafficRailContribution();
        Assert.False(rail.HideFromRail);
        Assert.False(rail.AlwaysOn);
        Assert.True(rail.DefaultEnabled);
    }

    [Fact]
    public void Descriptor_Sidebar_Kind_Is_Traffic()
    {
        // render-sidebar.js dispatches on this string; changing it
        // requires a matching arm in the switch / case there.
        var rail = new BowireTrafficRailContribution();
        Assert.Equal("traffic", rail.SidebarKind);
    }

    [Fact]
    public void Descriptor_Discovered_By_Registry()
    {
        // Force the assembly into the AppDomain so the reflection scan
        // picks it up (same pattern as the always-on rails in
        // BowireRailRegistryTests.Discover_Picks_Up_BuiltIn_Rails_*).
        _ = new BowireTrafficRailContribution().Id;

        var registry = BowireRailRegistry.Discover();
        var rail = registry.GetById("traffic");
        Assert.NotNull(rail);
        Assert.Equal("Traffic", rail!.DisplayName);
    }

    [Fact]
    public void Legacy_Proxy_And_Intercepted_Stay_In_Catalogue()
    {
        // Deprecation window: the old descriptors are still discoverable
        // so embedded hosts that explicitly reference them in DI keep
        // compiling and existing deep links (railMode=proxy) still
        // resolve through the catalogue. The visible-rail filter is the
        // HideFromRail flag, not removal from the registry.
        // Force the legacy assemblies into the AppDomain (the
        // discovery scan only sees loaded assemblies) — same pattern
        // as BowireRailRegistryTests.Discover_Picks_Up_BuiltIn_Rails_*.
        _ = new BowireTrafficRailContribution().Id;
        _ = new BowireProxyRailContribution().Id;
        _ = new BowireInterceptedRailContribution().Id;

        var registry = BowireRailRegistry.Discover();
        var proxy = registry.GetById("proxy");
        var intercepted = registry.GetById("intercepted");
        Assert.NotNull(proxy);
        Assert.NotNull(intercepted);
        Assert.True(proxy!.HideFromRail);
        Assert.True(intercepted!.HideFromRail);
    }
}
