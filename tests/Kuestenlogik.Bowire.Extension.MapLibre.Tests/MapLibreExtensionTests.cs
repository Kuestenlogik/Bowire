// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Semantics.Extensions;

namespace Kuestenlogik.Bowire.Extension.MapLibre.Tests;

/// <summary>
/// Sanity tests for the built-in MapLibre extension descriptor.
/// The descriptor is metadata-only (no behaviour), but several values
/// are load-bearing for the JS-side router: the kind string is what
/// the response-pane mounter pairs with detector output, the
/// capability flags drive viewer-vs-editor placement, and the resource
/// names anchor the asset-serving endpoint.
/// </summary>
public sealed class MapLibreExtensionTests
{
    [Fact]
    public void Id_Is_The_Stable_KuestenlogikPrefix()
    {
        var ext = new MapLibreExtension();
        Assert.Equal("kuestenlogik.maplibre", ext.Id);
    }

    [Fact]
    public void Declares_Coordinate_Wgs84_Kind()
    {
        var ext = new MapLibreExtension();
        Assert.Single(ext.Kinds);
        Assert.Equal("coordinate.wgs84", ext.Kinds[0]);
    }

    [Fact]
    public void Declares_Viewer_And_Editor_Capabilities()
    {
        var ext = new MapLibreExtension();
        Assert.True(ext.Capabilities.HasFlag(ExtensionCapabilities.Viewer));
        Assert.True(ext.Capabilities.HasFlag(ExtensionCapabilities.Editor));
    }

    [Fact]
    public void BowireApi_Range_Is_OneDotX()
    {
        var ext = new MapLibreExtension();
        Assert.Equal("1.x", ext.BowireApiRange);
    }

    [Fact]
    public void Bundle_And_Styles_Resource_Names_Point_At_Vendored_Files()
    {
        var ext = new MapLibreExtension();
        Assert.Equal("wwwroot/js/widgets/map.js", ext.BundleResourceName);
        Assert.Equal("wwwroot/maplibre/maplibre-gl.css", ext.StylesResourceName);
    }

    [Fact]
    public void MapLibre_Js_And_License_Listed_As_Additional_Assets()
    {
        var ext = new MapLibreExtension();
        Assert.Contains("wwwroot/maplibre/maplibre-gl.js", ext.AdditionalAssetNames);
        Assert.Contains("wwwroot/maplibre/LICENSE", ext.AdditionalAssetNames);
    }
}

/// <summary>
/// Discovery-sweep tests for <see cref="BowireExtensionRegistry"/>. The
/// production code uses it as a singleton cache; the registry itself
/// returns a fresh instance per <see cref="BowireExtensionRegistry.Discover"/>
/// call, which the tests rely on.
/// </summary>
public sealed class BowireExtensionRegistryTests
{
    [Fact]
    public void Discover_Finds_The_BuiltIn_MapLibre_Extension()
    {
        var registry = BowireExtensionRegistry.Discover();
        var map = registry.GetUiExtension("kuestenlogik.maplibre");
        Assert.NotNull(map);
        Assert.Equal("kuestenlogik.maplibre", map!.Id);
        Assert.Equal("coordinate.wgs84", map.Kinds[0]);
    }

    [Fact]
    public void Discover_Maps_Extensions_To_Their_Declaring_Assembly()
    {
        var registry = BowireExtensionRegistry.Discover();
        var assembly = registry.GetDeclaringAssembly("kuestenlogik.maplibre");
        Assert.NotNull(assembly);
        Assert.Equal(typeof(MapLibreExtension).Assembly, assembly);
    }

    [Fact]
    public void GetUiExtension_Returns_Null_For_Unknown_Id()
    {
        var registry = BowireExtensionRegistry.Discover();
        Assert.Null(registry.GetUiExtension("not.a.real.extension"));
    }
}

/// <summary>
/// Phase 3-R offline-mode lockdown — the map widget renders correctly
/// without reaching any external host when <c>Bowire:MapTileUrl</c> is
/// not configured. Bowire has no JS test runner, so the contract is
/// validated by reading the widget bundle out of the extension's
/// embedded resources and asserting structural invariants against the
/// MapLibre style declarations it emits.
/// <para>
/// The tests pin the offline-default style's shape — no <c>glyphs</c>
/// URL, no <c>sprite</c> URL, no external <c>http(s)://</c> reference
/// anywhere in the bundle except inside comments or upstream license
/// banners. Any future style tweak that re-introduces an external URL
/// fails CI before the no-network guarantee leaks.
/// </para>
/// </summary>
public sealed class MapLibreOfflineLockdownTests
{
    private static readonly Lazy<string> WidgetBundle = new(LoadWidgetBundle);

    [Fact]
    public void Blank_Style_Has_No_Glyphs_Url()
    {
        // The bowireMapBlankStyle helper used to declare a
        // `glyphs: 'about:blank/...'` stub so a future labelled-pin
        // style would work without a config change. Phase 3-R removed
        // it: with no symbol/text-field layers in the widget, glyphs
        // are dead weight at best and an offline-mode leak at worst
        // (MapLibre would still resolve the URL template if the field
        // were present). Pin the absence — function body must not
        // mention `glyphs:`.
        var body = ExtractFunctionBody(WidgetBundle.Value, "bowireMapBlankStyle");
        Assert.False(
            body.Contains("glyphs", StringComparison.Ordinal),
            "bowireMapBlankStyle declares a `glyphs` field — offline-mode style must not reach for glyph PBFs.");
    }

    [Fact]
    public void Blank_Style_Has_No_Sprite_Url()
    {
        // Same lockdown rule for sprite atlases. The circle-layer
        // rendering path doesn't need sprites; declaring one would
        // produce a request to the spritesheet URL the first time the
        // map paints.
        var body = ExtractFunctionBody(WidgetBundle.Value, "bowireMapBlankStyle");
        Assert.False(
            body.Contains("sprite", StringComparison.Ordinal),
            "bowireMapBlankStyle declares a `sprite` field — offline-mode style must not reach for a sprite atlas.");
    }

    [Fact]
    public void Tile_Url_Style_Has_No_Glyphs_Or_Sprite_Url()
    {
        // The tile-URL branch (Bowire:MapTileUrl set) also stays free of
        // glyphs / sprite because the workbench's pin rendering doesn't
        // use either. The tile fetch itself is the only network egress
        // — adding a glyph or sprite source would silently widen the
        // egress surface. Find the bowireMapViewerMount function and
        // check the tile-URL branch.
        var stripped = StripJsComments(WidgetBundle.Value);
        // Find the section where `if (tileUrl) { ... } else { ... }` is.
        var pattern = new Regex(
            @"if\s*\(\s*tileUrl\s*\)\s*\{[\s\S]*?else\s*\{",
            RegexOptions.Singleline);
        var match = pattern.Match(stripped);
        Assert.True(match.Success, "tileUrl branch not found in widget bundle");
        var branch = match.Value;
        Assert.False(
            branch.Contains("glyphs", StringComparison.Ordinal),
            "Tile-URL style branch declares `glyphs` — would widen offline-mode egress.");
        Assert.False(
            branch.Contains("sprite", StringComparison.Ordinal),
            "Tile-URL style branch declares `sprite` — would widen offline-mode egress.");
    }

    [Fact]
    public void Widget_Bundle_Contains_No_External_Origin_References()
    {
        // The widget bundle is a self-contained JS file that gets
        // served from `/api/ui/extensions/kuestenlogik.maplibre/map.js`.
        // It must NOT reach for any external http(s):// origin at
        // runtime; the asset endpoints (baseUrl prefix + path) are all
        // same-origin by construction.
        //
        // Comments and JSDoc references are excluded by stripping line
        // and block comments before the regex pass. Empty-string URLs
        // like attribution: '' are fine.
        var bundle = WidgetBundle.Value;
        var stripped = StripJsComments(bundle);

        // Match http:// or https:// followed by anything non-whitespace,
        // not inside a string-templated path segment like '{...}'. A
        // matched URL whose first character after the scheme is a
        // placeholder ({fontstack}, {z}, {x}) belongs to a style
        // template — but Phase 3-R removed every such template, so any
        // hit is a regression.
        var urlPattern = new Regex(@"https?://[^\s'""<>]+", RegexOptions.Multiline);
        var hits = urlPattern.Matches(stripped);
        Assert.Empty(hits);
    }

    [Fact]
    public void Map_Viewer_Opts_Into_Multi_Map_Editor_Stays_Single()
    {
        var bundle = WidgetBundle.Value;
        // Phase 3.2 + Phase 3-R — registration shape pinned in the
        // widget's own bundle (moved here from the core
        // SelectionModeJsContractTests when the widget moved out of
        // core). The viewer-first / editor-second order is significant
        // because reshaping that block is the same kind of risk as
        // reshaping the public ctx surface.
        var pattern = new Regex(
            @"viewer:\s*\{[^}]*selectionMode:\s*'multi'[^}]*mount:\s*bowireMapViewerMount" +
            @"[^}]*\}[^}]*editor:\s*\{[^}]*selectionMode:\s*'single'[^}]*mount:\s*bowireMapEditorMount",
            RegexOptions.Singleline);
        Assert.Matches(pattern, bundle);
    }

    [Fact]
    public void Widget_Pins_Use_Circle_Layer_Not_Symbol_With_Text_Field()
    {
        // The structural shape that keeps the offline guarantee
        // unbreakable: pin rendering uses MapLibre's `circle` layer
        // type, never a `symbol` layer with `text-field` (which would
        // demand a glyphs source) or `icon-image` (which would demand
        // a sprite atlas). Pin both sides — `type: 'circle'` present,
        // and no `text-field` or `icon-image` outside of documentation
        // comments.
        var bundle = WidgetBundle.Value;
        Assert.Contains("type: 'circle'", bundle, StringComparison.Ordinal);
        var stripped = StripJsComments(bundle);
        Assert.False(
            stripped.Contains("text-field", StringComparison.Ordinal),
            "Widget references `text-field` outside comments — would require a glyph source.");
        Assert.False(
            stripped.Contains("icon-image", StringComparison.Ordinal),
            "Widget references `icon-image` outside comments — would require a sprite atlas.");
    }

    /// <summary>
    /// Extract the body of a named JS function from a source string —
    /// brace-matched scanner, robust against nested objects /
    /// arrow-fns. Throws when the function isn't found so a rename
    /// drops a loud failure instead of a silent test pass.
    /// </summary>
    private static string ExtractFunctionBody(string source, string name)
    {
        var anchor = "function " + name;
        var start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Function `{name}` not found in widget bundle.");
        var brace = source.IndexOf('{', start);
        Assert.True(brace >= 0, $"Opening brace for `{name}` not found.");
        int depth = 0;
        for (int i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}') { depth--; if (depth == 0) return source.Substring(brace, i - brace + 1); }
        }
        Assert.Fail($"Unbalanced braces in function `{name}`.");
        return string.Empty;
    }

    /// <summary>
    /// Strip JS line comments (`// ...`) and block comments
    /// (<c>/* ... */</c>) so the URL-presence pass doesn't trip on
    /// architecture-doc references in the bundle's header. Conservative
    /// — preserves string literals (URLs inside quotes still match).
    /// </summary>
    private static string StripJsComments(string source)
    {
        // Strip block comments first (greedy across newlines).
        source = Regex.Replace(source, @"/\*[\s\S]*?\*/", string.Empty);
        // Strip line comments — anchored at the start of a line OR
        // after whitespace, so URLs in strings like 'https://...' are
        // preserved (the // inside a string is not a comment, but we'd
        // have stripped surrounding context already; the URL itself
        // still passes through). This is good enough for the
        // bundle-source we control end-to-end.
        var sb = new System.Text.StringBuilder(source.Length);
        bool inString = false;
        char stringQuote = '\0';
        for (int i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < source.Length) { sb.Append(source[++i]); continue; }
                if (c == stringQuote) inString = false;
                continue;
            }
            if (c == '\'' || c == '"' || c == '`')
            {
                inString = true;
                stringQuote = c;
                sb.Append(c);
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                // Skip until newline
                while (i < source.Length && source[i] != '\n') i++;
                if (i < source.Length) sb.Append('\n');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string LoadWidgetBundle()
    {
        // The bundle is the EmbeddedResource shipped on the extension
        // assembly — same shape as the core's bowire.js. Use the dotted
        // form MSBuild's default <EmbeddedResource> naming emits.
        var assembly = typeof(MapLibreExtension).Assembly;
        const string resourceName = "Kuestenlogik.Bowire.Extension.MapLibre.wwwroot.js.widgets.map.js";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}. " +
                "Try a clean rebuild of Kuestenlogik.Bowire.Extension.MapLibre.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

