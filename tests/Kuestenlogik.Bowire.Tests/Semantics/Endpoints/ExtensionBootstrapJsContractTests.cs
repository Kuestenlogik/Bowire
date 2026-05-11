// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Tests.Semantics.Endpoints;

/// <summary>
/// Phase 3-R — JS-side contract pins for the external-extension
/// bootstrap (<c>bowireLoadExternalExtensions</c>) and the
/// graceful-degradation placeholder card (<c>bowireRenderPlaceholder</c>)
/// that mounts when an annotation kind exists but no extension has
/// registered against it. Same regex-over-source approach used by
/// <c>SelectionModeJsContractTests</c> and
/// <c>SemanticsMenuJsContractTests</c> — Bowire has no JS test runner,
/// so structural invariants over the concat'd bundle are the cheapest
/// way to fail loudly when the contract drifts.
/// </summary>
public sealed class ExtensionBootstrapJsContractTests
{
    private static readonly Lazy<string> JsBundle = new(LoadJsBundle);

    [Fact]
    public void Bootstrap_Fetches_The_UiExtensions_Listing()
    {
        // The bootstrap routes through `/api/ui/extensions`, the same
        // endpoint the C# `BowireSemanticsEndpoints` enumeration serves.
        // The bundle prefix (config.prefix) is prepended at runtime so
        // sub-path-hosted deployments (e.g. `/bowire/`) work without
        // configuration.
        Assert.Contains(
            "'/api/ui/extensions'",
            JsBundle.Value,
            StringComparison.Ordinal);
        Assert.Contains(
            "bowireLoadExternalExtensions",
            JsBundle.Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Bootstrap_Injects_Script_Tag_For_Each_Bundle()
    {
        // Each declared extension's bundle URL becomes a `<script>` tag
        // appended to `document.head`. The async=false flag preserves
        // register-order across extensions; if two extensions race the
        // load, the framework's by-id dedupe rule wins anyway, but
        // ordered loads make hot-reload debugging more predictable.
        var bundle = JsBundle.Value;
        Assert.Matches(
            new Regex(@"createElement\('script'\)", RegexOptions.Singleline),
            bundle);
        Assert.Contains("script.async = false", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Bootstrap_Injects_Link_Tag_For_Optional_Stylesheet()
    {
        // The descriptor's stylesUrl is optional — only injected when
        // present, deduped by id so reload doesn't double-inject.
        var bundle = JsBundle.Value;
        Assert.Matches(
            new Regex(@"createElement\('link'\)", RegexOptions.Singleline),
            bundle);
        Assert.Contains("rel = 'stylesheet'", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Placeholder_Card_Renders_For_Coordinate_Kinds_When_Unregistered()
    {
        // The kind→package-id table maps the well-known coordinate kinds
        // to Kuestenlogik.Bowire.Extension.MapLibre — that's the
        // ground-truth recommendation when the auto-detector writes
        // coordinate.latitude / coordinate.longitude but no map widget
        // is installed.
        var bundle = JsBundle.Value;
        Assert.Contains(
            "'coordinate.wgs84': 'Kuestenlogik.Bowire.Extension.MapLibre'",
            bundle,
            StringComparison.Ordinal);
        Assert.Contains(
            "'coordinate.latitude': 'Kuestenlogik.Bowire.Extension.MapLibre'",
            bundle,
            StringComparison.Ordinal);
        Assert.Contains(
            "'coordinate.longitude': 'Kuestenlogik.Bowire.Extension.MapLibre'",
            bundle,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Placeholder_Message_Names_The_Kind_And_The_Package()
    {
        // The placeholder is generic across kinds — the message text
        // interpolates the kind so the user knows what data the
        // suggested package would render.
        var bundle = JsBundle.Value;
        Assert.Contains("function bowireRenderPlaceholder", bundle, StringComparison.Ordinal);
        Assert.Contains("Install ", bundle, StringComparison.Ordinal);
        // The kind name shows up inside backticks in the tail message.
        Assert.Matches(
            new Regex(@"to render `'\s*\+\s*kind\s*\+\s*'`"),
            bundle);
    }

    [Fact]
    public void Placeholder_Includes_Copy_To_Clipboard_Button()
    {
        var bundle = JsBundle.Value;
        // The copy button is a UX nicety — clicking it puts the
        // package id on the clipboard so the user can paste it into
        // `dotnet add package …`.
        Assert.Contains("clipboard", bundle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("navigator.clipboard", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Placeholder_Skips_When_Kind_Is_Covered_By_Pairing_Required()
    {
        // When an extension is registered whose `pairing.required`
        // list contains the unregistered companion kind (e.g. MapLibre
        // declaring required: ['coordinate.latitude',
        // 'coordinate.longitude']), the placeholder is suppressed
        // because the extension owns the umbrella view. The covered-
        // skip pass keeps the placeholder restricted to genuinely
        // orphan kinds.
        Assert.Matches(
            new Regex(@"pairing\.required\s*&&\s*[^;]*indexOf\(\s*kind\s*\)\s*>=\s*0"),
            JsBundle.Value);
    }

    [Fact]
    public void Placeholder_Dedupes_By_Suggestion_Id()
    {
        // The latitude + longitude both map to the SAME package
        // (Kuestenlogik.Bowire.Extension.MapLibre); without a dedupe
        // pass the workbench would render two identical "Install …"
        // cards next to each other.
        Assert.Contains(
            "placeholdersEmitted",
            JsBundle.Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Map_Widget_Is_Not_Concatenated_Into_Core_Bundle_Anymore()
    {
        // Phase 3-R proof — the map widget moved to the MapLibre
        // extension package; core's bowire.js must NOT contain the
        // bundle's signature symbols. If a future refactor re-bakes
        // the widget into core, this test fails and the dev gets a
        // chance to reconsider the bundle-size win.
        var bundle = JsBundle.Value;
        Assert.False(
            bundle.Contains("bowireMapViewerMount", StringComparison.Ordinal),
            "core bowire.js still contains bowireMapViewerMount — the MapLibre widget should ship in its own package.");
        Assert.False(
            bundle.Contains("bowireMapEditorMount", StringComparison.Ordinal),
            "core bowire.js still contains bowireMapEditorMount — the MapLibre widget should ship in its own package.");
        // The widget's MapLibre-script tag injection must not survive in core either.
        Assert.False(
            bundle.Contains("bowireLoadMapLibre", StringComparison.Ordinal),
            "core bowire.js still contains the MapLibre script loader — the widget should ship in its own package.");
    }

    private static string LoadJsBundle()
    {
        // The bundle is an EmbeddedResource on Kuestenlogik.Bowire.
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
