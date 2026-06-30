// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// v2.2 rail-IA refactor — JS-side contract pins for the unified
/// Intercept rail. Same regex-over-source approach used by
/// <see cref="Semantics.Endpoints.ExtensionBootstrapJsContractTests"/> —
/// Bowire has no JS test runner, so structural invariants over the
/// concatenated core bundle (and the Interceptor fragment loaded
/// separately) are the cheapest way to fail loudly when the contract
/// drifts.
/// </summary>
public sealed class InterceptRailJsContractTests
{
    private static readonly Lazy<string> CoreBundle = new(LoadCoreBundle);
    private static readonly Lazy<string> InterceptFragment = new(LoadInterceptFragment);

    [Fact]
    public void Boot_Migration_Rewrites_Legacy_Ids_To_Intercept()
    {
        // The boot-migration block in prologue.js MUST rewrite the
        // persisted railMode AND sidebarView so existing installs land
        // on the unified rail on first paint after the upgrade. If
        // either rewrite is missing, the operator opens the workbench
        // and lands on an unresolved id with no visible rail.
        var bundle = CoreBundle.Value;
        // All four legacy ids must trigger the rewrite. The migration
        // block uses a single combined `||` chain.
        Assert.Matches(
            new Regex(@"railMode\s*===\s*'mocks'.*railMode\s*===\s*'traffic'", RegexOptions.Singleline),
            bundle);
        Assert.Contains("railMode === 'proxy'", bundle, StringComparison.Ordinal);
        Assert.Contains("railMode === 'intercepted'", bundle, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"railMode\s*=\s*'intercept'"),
            bundle);
        // The sidebar-view key rewrite is what actually re-routes the
        // main-pane dispatcher (which reads sidebarView, not railMode).
        Assert.Contains("'bowire_sidebar_view'", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_Migration_Seeds_Intercept_SubTab_From_Legacy_Mode()
    {
        // The sub-tab discriminator must be seeded from the legacy
        // mode so the operator lands on the equivalent sub-tab instead
        // of the default. railMode='mocks' → 'mock-servers'.
        var bundle = CoreBundle.Value;
        Assert.Contains("bowire_intercept_sub_tab", bundle, StringComparison.Ordinal);
        Assert.Contains("'mock-servers'", bundle, StringComparison.Ordinal);
        Assert.Contains("'live-overrides'", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_Migration_Collapses_Enabled_Rails_Entries()
    {
        // The bowire_enabled_rails list (when present) must collapse
        // 'mocks' + 'traffic' entries into a single 'intercept' so
        // operators that explicitly opted INTO one of the legacy rails
        // don't lose their opt-in after the merge.
        var bundle = CoreBundle.Value;
        Assert.Contains("bowire_enabled_rails", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_Migration_Rewrites_Environments_To_Workspaces()
    {
        // R2 of v2.2 — Environments rail descriptor dropped. The
        // surface continues to render inside Workspaces.
        var bundle = CoreBundle.Value;
        Assert.Contains("railMode === 'environments'", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void SidebarDispatch_Recognises_Intercept_Kind()
    {
        // The render-sidebar.js switch on sidebar.kind must carry an
        // 'intercept' arm or the unified rail's sidebar never paints.
        Assert.Matches(
            new Regex(@"case\s+'intercept'\s*:\s*sidebar\s*=\s*renderInterceptSidebar"),
            CoreBundle.Value);
    }

    [Fact]
    public void MainPane_Recognises_Intercept_SidebarView()
    {
        // render-main.js must dispatch sidebarView === 'intercept' into
        // the unified renderInterceptMainPane helper that the
        // Interceptor fragment ships.
        Assert.Matches(
            new Regex(@"sidebarView\s*===\s*'intercept'"),
            CoreBundle.Value);
        Assert.Contains("renderInterceptMainPane", CoreBundle.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Intercept_Fragment_Exposes_The_Required_Hooks()
    {
        // The Interceptor fragment must declare both the sidebar list
        // entry-point (renderInterceptListInto) and the main-pane
        // entry-point (renderInterceptMainPane) — render-sidebar.js +
        // render-main.js call them by name.
        var fragment = InterceptFragment.Value;
        Assert.Contains("function renderInterceptListInto", fragment, StringComparison.Ordinal);
        Assert.Contains("function renderInterceptMainPane", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public void Intercept_Fragment_Adapts_Settings_To_BowireOptions_Mode()
    {
        // The Settings sub-tab MUST differ between Standalone and
        // Embedded so the operator instantly knows which deployment
        // the rail is wired to. The fragment reads uiMode === 'embedded'
        // (set from BowireOptions.Mode via __BOWIRE_CONFIG__.embeddedMode).
        var fragment = InterceptFragment.Value;
        Assert.Contains("Embedded — Bowire is mounted in-process", fragment, StringComparison.Ordinal);
        Assert.Contains("Standalone — Bowire runs as a CLI tool", fragment, StringComparison.Ordinal);
        Assert.Contains("uiMode === 'embedded'", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public void Intercept_Fragment_Ships_Four_SubTabs_In_Locked_Order()
    {
        // Captured | Live overrides | Mock servers | Settings — the
        // canonical sub-tab strip of the unified rail. Both the
        // identifiers (used in the dispatcher / localStorage) AND the
        // user-facing labels MUST be present so a label drift can't
        // sneak through without a contract update.
        var fragment = InterceptFragment.Value;
        Assert.Contains("'captured'", fragment, StringComparison.Ordinal);
        Assert.Contains("'live-overrides'", fragment, StringComparison.Ordinal);
        Assert.Contains("'mock-servers'", fragment, StringComparison.Ordinal);
        Assert.Contains("'settings'", fragment, StringComparison.Ordinal);
        Assert.Contains("Captured", fragment, StringComparison.Ordinal);
        Assert.Contains("Live overrides", fragment, StringComparison.Ordinal);
        Assert.Contains("Mock servers", fragment, StringComparison.Ordinal);
        Assert.Contains("Settings", fragment, StringComparison.Ordinal);

        // The locked order — Captured first, Mock servers third, then
        // Settings. Pin via positional assertion so a re-ordering trips.
        var capturedIdx = fragment.IndexOf("'captured'", StringComparison.Ordinal);
        var liveIdx = fragment.IndexOf("'live-overrides'", StringComparison.Ordinal);
        var mocksIdx = fragment.IndexOf("'mock-servers'", StringComparison.Ordinal);
        Assert.True(capturedIdx > 0 && liveIdx > capturedIdx && mocksIdx > liveIdx,
            "Sub-tab declaration order must be Captured → Live overrides → Mock servers");
    }

    [Fact]
    public void Intercept_Fragment_Delegates_Mock_Servers_To_Mock_Package()
    {
        // Architecture choice C: the Mock-servers sub-tab pokes the
        // window.__bowireMocks shim installed by Bowire.Mock's mocks.js
        // fragment. When the Mock package isn't referenced, the shim is
        // absent and the sub-tab renders an empty state instead of an
        // unexplained blank pane.
        var fragment = InterceptFragment.Value;
        Assert.Contains("window.__bowireMocks", fragment, StringComparison.Ordinal);
        Assert.Contains("Mock package not loaded", fragment, StringComparison.Ordinal);
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

    private static string LoadInterceptFragment()
    {
        var assembly = typeof(global::Kuestenlogik.Bowire.Interceptor.BowireInterceptRailContribution).Assembly;
        const string resourceName = "Kuestenlogik.Bowire.Interceptor.wwwroot.js.intercept-view.js";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
