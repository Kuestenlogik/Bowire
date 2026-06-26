// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Phase 3-R / #315 — JS-side contract pins for the unified Traffic
/// rail. Same regex-over-source approach used by
/// <see cref="Semantics.Endpoints.ExtensionBootstrapJsContractTests"/> —
/// Bowire has no JS test runner, so structural invariants over the
/// concatenated core bundle (and the Rail.Traffic fragment loaded
/// separately) are the cheapest way to fail loudly when the contract
/// drifts.
/// </summary>
public sealed class TrafficRailJsContractTests
{
    private static readonly Lazy<string> CoreBundle = new(LoadCoreBundle);
    private static readonly Lazy<string> TrafficFragment = new(LoadTrafficFragment);

    [Fact]
    public void Boot_Migration_Rewrites_Proxy_And_Intercepted_To_Traffic()
    {
        // The boot-migration block in prologue.js MUST rewrite the
        // persisted railMode AND sidebarView so existing installs land
        // on the unified pane on first paint after the upgrade. If
        // either rewrite is missing, the operator opens the workbench
        // and lands on a HideFromRail descriptor with no visible icon.
        var bundle = CoreBundle.Value;
        Assert.Contains(
            "railMode === 'proxy' || railMode === 'intercepted'",
            bundle,
            StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"railMode\s*=\s*'traffic'"),
            bundle);
        // The sidebar-view key rewrite is what actually re-routes the
        // main-pane dispatcher (which reads sidebarView, not railMode).
        Assert.Contains(
            "'bowire_sidebar_view'",
            bundle,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SidebarDispatch_Recognises_Traffic_Kind()
    {
        // The render-sidebar.js switch on sidebar.kind must carry a
        // 'traffic' arm or the unified rail's sidebar never paints.
        Assert.Matches(
            new Regex(@"case\s+'traffic'\s*:\s*sidebar\s*=\s*renderTrafficSidebar"),
            CoreBundle.Value);
    }

    [Fact]
    public void MainPane_Recognises_Traffic_SidebarView()
    {
        // render-main.js must dispatch sidebarView === 'traffic' into
        // the unified renderTrafficMainPane helper that the Rail.Traffic
        // fragment ships.
        Assert.Matches(
            new Regex(@"sidebarView\s*===\s*'traffic'"),
            CoreBundle.Value);
        Assert.Contains(
            "renderTrafficMainPane",
            CoreBundle.Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Traffic_Fragment_Exposes_The_Required_Hooks()
    {
        // The Rail.Traffic fragment must declare both the sidebar list
        // entry-point (renderTrafficListInto) and the main-pane
        // entry-point (renderTrafficMainPane) — render-sidebar.js +
        // render-main.js call them by name.
        var fragment = TrafficFragment.Value;
        Assert.Contains("function renderTrafficListInto", fragment, StringComparison.Ordinal);
        Assert.Contains("function renderTrafficMainPane", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public void Traffic_Fragment_Adapts_Header_To_BowireOptions_Mode()
    {
        // The mode banner / header text MUST differ between Standalone
        // and Embedded so the operator instantly knows which deployment
        // the rail is wired to. The fragment reads uiMode === 'embedded'
        // (set from BowireOptions.Mode via __BOWIRE_CONFIG__.embeddedMode).
        var fragment = TrafficFragment.Value;
        Assert.Contains("Embedded middleware mode", fragment, StringComparison.Ordinal);
        Assert.Contains("Standalone proxy mode", fragment, StringComparison.Ordinal);
        Assert.Contains("uiMode === 'embedded'", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public void Traffic_Fragment_Ships_Three_SubTabs()
    {
        // Flows | Mock Rules | Settings — the canonical sub-tab strip
        // of the unified rail. The label strings are user-facing, so
        // assert on them directly.
        var fragment = TrafficFragment.Value;
        Assert.Contains("'flows'", fragment, StringComparison.Ordinal);
        Assert.Contains("'mocks'", fragment, StringComparison.Ordinal);
        Assert.Contains("'settings'", fragment, StringComparison.Ordinal);
        Assert.Contains("Mock Rules", fragment, StringComparison.Ordinal);
    }

    private static string LoadCoreBundle()
    {
        var assembly = typeof(global::Kuestenlogik.Bowire.BowireServiceCollectionExtensions).Assembly;
        const string resourceName = "Kuestenlogik.Bowire.wwwroot.bowire.js";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}. " +
                "The JS concat target may have failed; try `dotnet build`.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string LoadTrafficFragment()
    {
        var assembly = typeof(global::Kuestenlogik.Bowire.Rail.Traffic.BowireTrafficRailContribution).Assembly;
        const string resourceName = "Kuestenlogik.Bowire.Rail.Traffic.wwwroot.js.traffic-view.js";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
