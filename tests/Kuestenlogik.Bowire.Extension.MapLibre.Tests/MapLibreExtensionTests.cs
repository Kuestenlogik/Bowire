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
    public void Raster_Tile_Style_Has_No_Glyphs_Or_Sprite_Url()
    {
        // Raster-tile branch (operator opts into osm / satellite /
        // arbitrary tile URL) stays free of glyphs / sprite. The tile
        // fetch itself is the explicit egress the operator opted into;
        // adding a glyph or sprite source would silently widen the
        // surface. Locate the synthetic raster style the widget builds
        // for any `basemap.kind === 'raster'` spec and pin the absence.
        var stripped = StripJsComments(WidgetBundle.Value);
        // The raster-style construction is the
        // `basemap.kind === 'raster'` branch. Match from that condition
        // through the next `else`.
        var pattern = new Regex(
            @"basemap\.kind\s*===\s*'raster'[\s\S]*?else",
            RegexOptions.Singleline);
        var match = pattern.Match(stripped);
        Assert.True(match.Success, "raster-style branch not found in widget bundle");
        var branch = match.Value;
        Assert.False(
            branch.Contains("glyphs", StringComparison.Ordinal),
            "Raster-style branch declares `glyphs` — would widen offline-mode egress.");
        Assert.False(
            branch.Contains("sprite", StringComparison.Ordinal),
            "Raster-style branch declares `sprite` — would widen offline-mode egress.");
    }

    /// <summary>
    /// Opt-in basemap origins the widget intentionally references in
    /// <see cref="Widget_Bundle_Has_No_Unknown_External_Origins"/>.
    /// Each entry is a host that's only contacted when the operator
    /// explicitly sets <c>Bowire:MapBasemap</c> to the matching alias
    /// (or passes a custom tile/style URL); the default path stays on
    /// the offline blank style. Update this list when a new alias
    /// lands — the test reads from it directly so a bare URL literal
    /// in the bundle without a matching allowlist entry fails CI.
    /// </summary>
    private static readonly string[] AllowedExternalHosts =
    {
        // OSM raster tiles + attribution link, alias: 'osm'
        "tile.openstreetmap.org",
        "www.openstreetmap.org",
        // ESRI World Imagery satellite mosaic + attribution link,
        // alias: 'satellite'
        "server.arcgisonline.com",
        "www.esri.com",
        // MapLibre demotiles default vector style, alias: 'demotiles'
        // (also the implicit default when no basemap is configured)
        "demotiles.maplibre.org",
        // W3C XML namespace constants for inline SVG icons (MIL-2525C
        // affinity sprite frames). The string is a namespace identifier
        // baked into every well-formed SVG document — never fetched by
        // browsers, just compared as a literal — but the URL regex
        // can't tell that apart from a tile URL without context, so the
        // allowlist names it explicitly.
        "www.w3.org",
    };

    [Fact]
    public void Widget_Bundle_Has_No_Unknown_External_Origins()
    {
        // The widget bundle is a self-contained JS file that gets
        // served from `/api/ui/extensions/kuestenlogik.maplibre/map.js`.
        // The default (no-config) path renders against the bundled
        // demotiles style — the only origin reached without operator
        // opt-in. Named aliases (`osm` / `satellite`) add a small,
        // documented set of additional opt-in hosts. ANY external URL
        // in the bundle that doesn't match the allowlist above is a
        // regression: it would mean the widget started reaching for a
        // host the operator never explicitly enabled.
        //
        // Comments and JSDoc references are excluded by stripping line
        // and block comments before the regex pass.
        var stripped = StripJsComments(WidgetBundle.Value);

        var urlPattern = new Regex(@"https?://[^\s'""<>]+", RegexOptions.Multiline);
        var hits = urlPattern.Matches(stripped);

        var unknownOrigins = new List<string>();
        foreach (Match hit in hits)
        {
            var url = hit.Value;
            // Trim any trailing punctuation accidentally caught.
            url = url.TrimEnd(',', ';', ')', ']', '}', '.');
            // Extract the host portion (between `://` and the next `/`).
            var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0) { unknownOrigins.Add(url); continue; }
            var hostStart = schemeEnd + 3;
            var hostEnd = url.IndexOf('/', hostStart);
            var host = hostEnd < 0
                ? url.Substring(hostStart)
                : url.Substring(hostStart, hostEnd - hostStart);
            if (!AllowedExternalHosts.Contains(host, StringComparer.Ordinal))
            {
                unknownOrigins.Add(url);
            }
        }

        Assert.Empty(unknownOrigins);
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
    public void Widget_Pins_Do_Not_Require_Glyph_Source()
    {
        // The structural shape that keeps the offline guarantee
        // unbreakable: pin rendering may use a `symbol` layer (the
        // MIL-2525C affinity icons sit on one), but the bundle MUST
        // NOT use `text-field` — that single property is what forces
        // MapLibre to fetch glyph PBFs from the style's `glyphs` URL.
        // Icon-image is fine here because the widget registers its
        // sprite frames at runtime via `map.addImage` with inline SVG
        // data URLs (see bowireRegisterMapIcon + BOWIRE_MAP_AFFINITY_ICONS).
        // That path is the documented MapLibre escape hatch for
        // sprite-atlas-less symbol layers — addImage takes an
        // HTMLImageElement directly, the style's `sprite` URL stays
        // absent, and the renderer never reaches for an atlas.
        var bundle = WidgetBundle.Value;
        // The selection-halo layer keeps the circle path alive, so
        // the `circle` layer type still has to be present in the
        // bundle alongside the new symbol layer.
        Assert.Contains("type: 'circle'", bundle, StringComparison.Ordinal);
        Assert.Contains("type: 'symbol'", bundle, StringComparison.Ordinal);
        var stripped = StripJsComments(bundle);
        Assert.False(
            stripped.Contains("text-field", StringComparison.Ordinal),
            "Widget references `text-field` outside comments — would require a glyph source.");
    }

    [Fact]
    public void Widget_Symbol_Layer_Loads_Icons_From_Inline_Svg_Only()
    {
        // The icon-image expression on the symbol layer must resolve
        // every sprite name through `map.addImage`-registered inline
        // SVGs (data URLs) — not from a remote sprite atlas URL on the
        // style. Pin BOTH sides: (1) the icon-image expression names
        // the documented `bowire-affinity-*` sprite frames, and (2)
        // the bundle declares zero `sprite:` URL fields. Together,
        // these keep the symbol-layer addition from quietly leaking
        // egress to a sprite atlas host.
        var stripped = StripJsComments(WidgetBundle.Value);
        Assert.Contains("'bowire-affinity-friend'", stripped, StringComparison.Ordinal);
        Assert.Contains("'bowire-affinity-hostile'", stripped, StringComparison.Ordinal);
        Assert.Contains("'bowire-affinity-neutral'", stripped, StringComparison.Ordinal);
        Assert.Contains("'bowire-affinity-unknown'", stripped, StringComparison.Ordinal);
        // Self-built style objects must not declare a `sprite:` URL.
        // `bowireMapBlankStyle` and the raster-style branch are already
        // pinned for `sprite` absence individually; this is the
        // bundle-wide net.
        var spriteUrlPattern = new Regex(
            @"sprite\s*:\s*['""]https?://",
            RegexOptions.Singleline);
        Assert.False(
            spriteUrlPattern.IsMatch(stripped),
            "Widget bundle declares a `sprite:` URL field — addImage path should be the only sprite source.");
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

