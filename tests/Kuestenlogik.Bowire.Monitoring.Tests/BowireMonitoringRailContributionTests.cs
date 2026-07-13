// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// Coverage for the Monitoring rail descriptor (#102) — the static
/// contract the workbench shell consumes. The renderers themselves live
/// in the JS fragment (monitoring.js) and are covered by the e2e spec
/// (tests/e2e/monitoring-rail.spec.ts).
/// </summary>
public class BowireMonitoringRailContributionTests
{
    [Fact]
    public void Descriptor_Has_Stable_MonitoringId()
    {
        // localStorage.bowire_rail_mode values + deep links key off this.
        var rail = new BowireMonitoringRailContribution();
        Assert.Equal("monitoring", rail.Id);
    }

    [Fact]
    public void Descriptor_Visible_And_Optional()
    {
        IBowireRailContribution rail = new BowireMonitoringRailContribution();
        Assert.False(rail.HideFromRail);
        Assert.False(rail.AlwaysOn);
        Assert.True(rail.DefaultEnabled);
        // The ledger root is machine-global — the rail must stay
        // reachable without an active workspace.
        Assert.False(rail.RequiresWorkspace);
    }

    [Fact]
    public void Descriptor_Renderer_Keys_Match_The_Fragment_Registrations()
    {
        // monitoring.js registers exactly these two keys on
        // window.__bowireRailRenderers; a rename must change both sides.
        IBowireRailContribution rail = new BowireMonitoringRailContribution();
        Assert.Equal("monitoringSidebar", rail.SidebarRendererKey);
        Assert.Equal("monitoringMain", rail.MainPaneRendererKey);
    }

    [Fact]
    public void Descriptor_Sorts_Into_The_Quality_Group()
    {
        // Between Benchmarks (1000, quality) and Security (1100,
        // hardening) — passive health sits with the quality surfaces.
        var rail = new BowireMonitoringRailContribution();
        Assert.Equal("quality", rail.Group);
        Assert.InRange(rail.SortIndex, 1001, 1099);
    }
}
