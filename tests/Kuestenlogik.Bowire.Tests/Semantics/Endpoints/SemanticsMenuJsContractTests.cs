// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Tests.Semantics.Endpoints;

/// <summary>
/// Phase 4 — JS-side contract tests for the right-click semantics menu.
/// Bowire has no JS test runner, so the contract is validated by reading
/// the embedded bundle and asserting structural invariants against it.
/// Each test pins one specific guarantee — accept-action is gated on
/// auto/plugin source, the built-in semantic-tag list is complete, the
/// persistence-default localStorage key is the one the ADR pins, the
/// scope picker offers all three modes, the companion-field map covers
/// the documented pairs.
/// </summary>
public sealed class SemanticsMenuJsContractTests
{
    private static readonly Lazy<string> JsBundle = new(LoadJsBundle);

    [Fact]
    public void Bundle_Contains_All_BuiltIn_Semantic_Tags()
    {
        var bundle = JsBundle.Value;
        // Mirror of BuiltInSemanticTags — if a new built-in lands on
        // the C# side, this list MUST be updated in lockstep with the
        // JS source. The test exists to catch the drift.
        string[] expected =
        [
            "coordinate.latitude",
            "coordinate.longitude",
            "coordinate.ecef.x",
            "coordinate.ecef.y",
            "coordinate.ecef.z",
            "image.bytes",
            "image.mime-type",
            "audio.bytes",
            "audio.sample-rate",
            "timeseries.timestamp",
            "timeseries.value",
            "table.row-array",
        ];
        foreach (var kind in expected)
        {
            Assert.Contains(kind, bundle, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Persistence_Default_LocalStorage_Key_Is_Pinned()
    {
        // The ADR pins the localStorage key as
        // `bowire_semantics_persist_default`. Renaming it would lose
        // user preferences across upgrades, which is a contract break.
        Assert.Contains("bowire_semantics_persist_default",
            JsBundle.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Persistence_Default_Allowed_Values_Are_Session_User_Project()
    {
        var bundle = JsBundle.Value;
        // The allowed set is checked at read time so an old localStorage
        // value typed in by hand doesn't fall through to the file tier
        // by accident.
        Assert.Matches(
            new Regex(@"BOWIRE_PERSIST_DEFAULT_ALLOWED\s*=\s*\{[^}]*session[^}]*user[^}]*project",
                RegexOptions.Singleline),
            bundle);
    }

    [Fact]
    public void Scope_Picker_Offers_All_Three_Modes()
    {
        var bundle = JsBundle.Value;
        Assert.Contains("'this-discriminator'", bundle, StringComparison.Ordinal);
        Assert.Contains("'this-method-where-path-exists'", bundle, StringComparison.Ordinal);
        Assert.Contains("'this-method-all-matching-path-names'", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Suppress_Writes_Explicit_None_Tag()
    {
        // The menu's Suppress action must call into bowireWriteAnnotation
        // with the literal 'none' tag (matching BuiltInSemanticTags.None
        // on the C# side). A copy-paste rename to e.g. 'suppressed'
        // would silently break the user-vs-auto suppression chain.
        Assert.Matches(
            new Regex(@"bowireWriteAnnotation\(opts,\s*'none'\)"),
            JsBundle.Value);
    }

    [Fact]
    public void Companion_Map_Covers_All_Documented_Pairs()
    {
        var bundle = JsBundle.Value;
        // Each entry in bowireSemanticCompanions has to be present
        // for the companion-field suggestion mechanism to fire.
        Assert.Matches(
            new Regex(@"'coordinate\.latitude':\s*\[\s*'coordinate\.longitude'\s*\]"),
            bundle);
        Assert.Matches(
            new Regex(@"'coordinate\.longitude':\s*\[\s*'coordinate\.latitude'\s*\]"),
            bundle);
        Assert.Matches(
            new Regex(@"'coordinate\.ecef\.x':\s*\[[^\]]*'coordinate\.ecef\.y'[^\]]*'coordinate\.ecef\.z'",
                RegexOptions.Singleline),
            bundle);
        Assert.Matches(
            new Regex(@"'image\.bytes':\s*\[\s*'image\.mime-type'\s*\]"),
            bundle);
        Assert.Matches(
            new Regex(@"'audio\.bytes':\s*\[\s*'audio\.sample-rate'\s*\]"),
            bundle);
    }

    [Fact]
    public void Menu_Closes_On_Escape_Key()
    {
        // Pin the Escape-handler so a future refactor that drops it
        // would fail loudly — the contract is "Escape closes the menu."
        Assert.Matches(
            new Regex(@"e\.key\s*===\s*'Escape'"),
            JsBundle.Value);
    }

    [Fact]
    public void Menu_Uses_Position_Fixed_With_Viewport_Clamping()
    {
        var bundle = JsBundle.Value;
        // The menu floats over the response pane via `position: fixed`
        // so overflow:hidden parents don't crop it. The clamp math
        // reads getBoundingClientRect after the first render and
        // shifts to keep the menu inside the viewport — pin both pieces.
        Assert.Matches(
            new Regex(@"menu\.style\.position\s*=\s*'fixed'"),
            bundle);
        Assert.Matches(
            new Regex(@"getBoundingClientRect\(\)"),
            bundle);
    }

    [Fact]
    public void Write_Endpoint_Posts_To_Phase4_Route()
    {
        // The semantics-menu endpoint MUST be the Phase-4 route.
        Assert.Contains("/api/semantics/annotation",
            JsBundle.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Successful_Write_Dispatches_SemanticsChanged_Event()
    {
        // The render layer listens for `bowire:semantics-changed` to
        // re-fetch the effective schema and re-decorate badges.
        Assert.Contains("bowire:semantics-changed",
            JsBundle.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Badge_Source_Tiers_Have_Distinct_Class_Names()
    {
        var bundle = JsBundle.Value;
        // The badge class encodes the source tier — auto / plugin /
        // user. The CSS uses those exact suffixes to colour the badge.
        // A typo that shipped just "bowire-semantics-badge" with no
        // tier would defeat the colour-coding entirely.
        Assert.Contains("bowire-semantics-badge-", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_Press_Is_Wired_For_Mobile_Access()
    {
        var bundle = JsBundle.Value;
        // touchstart → long-press timer → open menu is the mobile
        // alternative to right-click. Drop touchstart and mobile
        // users lose access to the framework's manual override.
        Assert.Contains("touchstart", bundle, StringComparison.Ordinal);
        Assert.Contains("BOWIRE_LONG_PRESS_MS", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Decorator_Is_Exposed_On_The_Public_Menu_Surface()
    {
        var bundle = JsBundle.Value;
        Assert.Contains("__bowireSemanticsMenu", bundle, StringComparison.Ordinal);
        Assert.Contains("decorate: bowireDecorateResponseTreeForSemantics",
            bundle, StringComparison.Ordinal);
    }

    private static string LoadJsBundle()
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
}
