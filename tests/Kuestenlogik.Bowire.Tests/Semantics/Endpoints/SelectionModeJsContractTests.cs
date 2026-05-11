// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Tests.Semantics.Endpoints;

/// <summary>
/// Phase 3.2 — JS-side `selectionMode` capability declaration on the
/// `BowireExtensions.register({...})` payload and the per-ctx filter
/// that truncates `selection$` snapshots to `[lastSelected]` for
/// `'single'`-mode viewers.
/// <para>
/// Bowire has no JS test runner, so the contract is validated by
/// reading the embedded bundle and asserting structural invariants
/// against it. Each test pins one specific guarantee that the
/// extension-author contract depends on; if any of these regress,
/// every consumer of the v1.0 ctx surface would silently lose a
/// feature instead of getting a loud failure.
/// </para>
/// </summary>
public sealed class SelectionModeJsContractTests
{
    private static readonly Lazy<string> JsBundle = new(LoadJsBundle);

    [Fact]
    public void Validation_Mentions_Both_Allowed_Values_And_Warns_On_Unknown()
    {
        var bundle = JsBundle.Value;
        // The validator must explicitly check for both legal values so
        // a copy-paste removal of 'multi' doesn't silently coerce.
        Assert.Contains("'single'", bundle, StringComparison.Ordinal);
        Assert.Contains("'multi'", bundle, StringComparison.Ordinal);
        // And the unknown-value path must console.warn with the
        // offending mode in the message — not a silent drop.
        Assert.Matches(
            new Regex(@"console\.warn\([^)]*unknown selectionMode", RegexOptions.Singleline),
            bundle);
    }

    [Fact]
    public void Default_Is_Single_When_Omitted()
    {
        // bowireNormaliseSelectionMode assigns 'single' when missing so
        // the rest of the framework can treat the field as authoritative.
        Assert.Matches(
            new Regex(@"block\.selectionMode\s*=\s*'single'"),
            JsBundle.Value);
    }

    [Fact]
    public void Filter_Function_Is_Exposed_For_Tests_And_Pure()
    {
        var bundle = JsBundle.Value;
        // Pure-function name surfaced on the framework test seam — the
        // way unit-test code (and a future JS runner) reaches into the
        // selection-mode logic without spinning up a real DOM.
        Assert.Contains("_applySelectionMode", bundle, StringComparison.Ordinal);
        Assert.Contains("function bowireApplySelectionMode", bundle, StringComparison.Ordinal);
    }

    // Phase 3-R — `Map_Viewer_Opts_Into_Multi_Map_Editor_Stays_Single`
    // moved to the Kuestenlogik.Bowire.Extension.MapLibre.Tests project
    // alongside the rest of the MapLibre descriptor tests. The
    // selectionMode contract for the framework's pure-filter functions
    // is core, but the map widget's registration shape rides with the
    // package that ships it.

    [Fact]
    public void Ctx_Mount_Propagates_SelectionMode_From_Viewer_Block()
    {
        // bowireMakeViewerCtx receives the viewer's selectionMode via
        // opts.selectionMode — this is the wire that makes filter
        // state per-ctx (and so two widgets on the same method stay
        // independent).
        Assert.Contains(
            "selectionMode: ext.viewer && ext.viewer.selectionMode",
            JsBundle.Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Filter_State_Is_Per_Ctx_Closure_Not_Module_Global()
    {
        var bundle = JsBundle.Value;
        // The filter ledger lives inside bowireMakeViewerCtx's closure;
        // a refactor that lifted it to module scope would break the
        // "two widgets, two ctxs, independent state" guarantee. Pin
        // the local-var-inside-function shape.
        Assert.Matches(
            new Regex(
                @"function bowireMakeViewerCtx\([^)]*\)\s*\{[\s\S]*?" +
                @"var selectionFilterState\s*=\s*\{\s*prev:\s*\[\]"),
            bundle);
    }

    private static string LoadJsBundle()
    {
        // The bundle is an EmbeddedResource on Kuestenlogik.Bowire. We
        // can't reach BowireHtmlGenerator's private reader from outside
        // the assembly (it's file-local), so re-implement the trivial
        // manifest lookup here.
        var assembly = typeof(global::Kuestenlogik.Bowire.BowireServiceCollectionExtensions).Assembly;
        const string resourceName = "Kuestenlogik.Bowire.wwwroot.bowire.js";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}. " +
                "The JS concat target may have failed; try `dotnet build`.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
