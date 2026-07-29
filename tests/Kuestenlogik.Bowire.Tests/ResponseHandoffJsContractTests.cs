// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// #536 — JS-side contract pins for the response handoff ("Use this…").
/// Bowire has no JS test runner, so structural invariants over the
/// concatenated core bundle (and the Flows fragment, loaded separately)
/// are the cheapest way to fail loudly when the contract drifts. Same
/// regex-over-source approach as
/// <see cref="InterceptRailJsContractTests"/>.
///
/// The invariant that matters most here is DEGRADATION: three of the
/// four handoffs live in optional packages, and the mount sites run
/// inside render(), which has no try/catch. A bare reference to an
/// absent package's symbol on that path blanks the entire workbench.
/// </summary>
public sealed class ResponseHandoffJsContractTests
{
    private static readonly Lazy<string> CoreBundle = new(LoadCoreBundle);
    private static readonly Lazy<string> FlowsFragment = new(LoadFlowsFragment);

    [Fact]
    public void Handoff_Surface_Is_Present_In_The_Core_Bundle()
    {
        // The whole affordance is core — an embedded host that
        // references nothing but Kuestenlogik.Bowire still gets the
        // button and the menu; only the individual items degrade.
        var bundle = CoreBundle.Value;
        Assert.Contains("function bowireRenderHandoffButton", bundle, StringComparison.Ordinal);
        Assert.Contains("function bowireShowHandoffMenu", bundle, StringComparison.Ordinal);
        Assert.Contains("function bowireEnsureRecordingAndCapture", bundle, StringComparison.Ordinal);
        Assert.Contains("function bowireHandoffSnapshot", bundle, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("isRecording")]
    [InlineData("startRecording")]
    [InlineData("addRequestToFlowPicker")]
    [InlineData("addTargetToEnvelopePicker")]
    public void Optional_Package_Symbols_Are_Probed_At_Least_As_Often_As_They_Are_Called(string symbol)
    {
        // Every one of these identifiers is declared inside an OPTIONAL
        // package's fragment. When the package isn't referenced the
        // fragment is never spliced in, so the binding does not exist
        // at all and a bare reference throws ReferenceError. The core
        // bundle must therefore carry at least one `typeof` presence
        // probe per call site.
        var code = StripLineComments(CoreBundle.Value);
        var probes = Regex.Count(code, @"\btypeof\s+" + Regex.Escape(symbol) + @"\b");
        var calls = Regex.Count(code, @"\b" + Regex.Escape(symbol) + @"\s*\(");
        Assert.True(
            probes >= calls,
            $"'{symbol}' is called {calls}x but only probed with typeof {probes}x in the core bundle. "
            + "Every call into an optional package needs a presence guard — an unguarded "
            + "ReferenceError inside render() blanks the whole workbench.");
    }

    [Fact]
    public void Mock_Host_Is_Reached_Only_Through_The_Window_Shim()
    {
        // Kuestenlogik.Bowire.Mock installs window.__bowireMocks. The
        // handoff must probe the shim AND the specific function it is
        // about to call, because an older Mock package could ship a
        // shim without startFromRecording.
        var bundle = CoreBundle.Value;
        Assert.Contains(
            "typeof window.__bowireMocks.startFromRecording === 'function'",
            bundle,
            StringComparison.Ordinal);
        Assert.Contains(
            "typeof mocks.startFromRecording !== 'function'",
            bundle,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Discover_Mount_Site_Is_Typeof_Guarded_And_Wrapped()
    {
        // renderResponsePane runs inside render(), which has no
        // try/catch of its own. Both the guard and the wrapper are
        // mandatory: the guard covers "fragment missing", the catch
        // covers "fragment present but threw".
        Assert.Matches(
            new Regex(
                @"typeof bowireRenderHandoffButton !== 'function'.{0,400}?"
                + @"bowireRenderHandoffButton\('discover'\).{0,400}?catch",
                RegexOptions.Singleline),
            CoreBundle.Value);
    }

    [Fact]
    public void Builder_Mount_Site_Is_Typeof_Guarded_And_Wrapped()
    {
        Assert.Matches(
            new Regex(
                @"typeof bowireRenderHandoffButton === 'function'.{0,400}?"
                + @"bowireRenderHandoffButton\('builder'\).{0,400}?catch",
                RegexOptions.Singleline),
            CoreBundle.Value);
    }

    [Fact]
    public void Button_Renders_An_Empty_Span_When_The_Last_Call_Did_Not_Succeed()
    {
        // Same do-nothing shape the neighbouring Compare button uses.
        // Without it the cluster would carry a live handoff for a
        // response that doesn't exist.
        Assert.Matches(
            new Regex(@"if \(!bowireLastCallSucceeded\(surface\)\) return el\('span'\);"),
            CoreBundle.Value);
    }

    [Fact]
    public void Request_Snapshot_Exists_Exactly_Once_And_The_Closure_Copy_Is_Gone()
    {
        // The snapshot used to live inside the "+ Add to…" menu closure
        // (as _snapshotRequest) with a second verbatim copy inlined in
        // the presets dropdown. Both were collapsed into one top-level
        // helper so the response handoff can't drift from the header
        // menu's idea of "the current request".
        var bundle = CoreBundle.Value;
        Assert.Single(Regex.Matches(bundle, @"function bowireSnapshotDiscoverRequest\b"));
        Assert.DoesNotContain("function _snapshotRequest", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Benchmark_Handoff_Does_Not_Synthesise_A_Click_On_The_Header_Menu()
    {
        // The execute split-button's "Run as benchmark…" item used to
        // do document.querySelector('#bowire-header-addto-btn').click().
        // That is a parallel path through an unrelated menu; it now
        // calls the handoff directly.
        var bundle = CoreBundle.Value;
        Assert.DoesNotContain("querySelector('#bowire-header-addto-btn')", bundle, StringComparison.Ordinal);
        Assert.Contains("bowireHandoffToBenchmark(snap, e.clientX, e.clientY)", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Flow_Picker_Lives_In_The_Flows_Package()
    {
        // flowsList / flowEditorSelectedId are `let`-declared inside the
        // Flows fragment and do not exist as bindings in a host without
        // the package, so the picker MUST stay on that side of the seam
        // and core may only reach it through the guarded function name.
        var fragment = FlowsFragment.Value;
        Assert.Contains("function addRequestToFlowPicker", fragment, StringComparison.Ordinal);
        Assert.Contains("showContextMenu(clientX, clientY, items)", fragment, StringComparison.Ordinal);

        Assert.DoesNotContain("function addRequestToFlowPicker", CoreBundle.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Drops <c>//</c>-to-end-of-line comments so the guard census below
    /// counts CODE, not prose. The concat step keeps comments verbatim
    /// (its "minify" pass only collapses blank lines), and the handoff
    /// code names its own guarded symbols in doc comments — without this
    /// a comment mentioning <c>startRecording()</c> would read as an
    /// unguarded call site. <c>://</c> is skipped so URLs survive.
    /// </summary>
    private static string StripLineComments(string js)
    {
        var sb = new System.Text.StringBuilder(js.Length);
        foreach (var line in js.Split('\n'))
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            while (idx > 0 && line[idx - 1] == ':')
            {
                idx = line.IndexOf("//", idx + 2, StringComparison.Ordinal);
            }

            sb.Append(idx >= 0 ? line[..idx] : line).Append('\n');
        }

        return sb.ToString();
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

    private static string LoadFlowsFragment()
    {
        var assembly = typeof(global::Kuestenlogik.Bowire.Flows.BowireFlowsRailContribution).Assembly;
        const string resourceName = "Kuestenlogik.Bowire.Flows.wwwroot.js.flows.js";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
