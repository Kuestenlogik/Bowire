// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics.Extensions;

/// <summary>
/// Bowire UI extension that mounts the MapLibre GL JS map widget on the
/// <c>coordinate.wgs84</c> semantic kind. Lives in the
/// <c>Kuestenlogik.Bowire.Extension.MapLibre</c> NuGet package — the first
/// concrete consumer of the extension API the ADR pins under "Extension
/// framework", and the dogfood proof that the same shape third parties use
/// is the shape Bowire's own widgets use.
/// </summary>
/// <remarks>
/// <para>
/// The descriptor only carries metadata + resource pointers; all rendering
/// logic lives in the JS bundle at <c>wwwroot/js/widgets/map.js</c>, which
/// is shipped as an embedded resource on this assembly and served at
/// <c>/api/ui/extensions/kuestenlogik.maplibre/map.js</c>. The JS bundle
/// calls <c>window.BowireExtensions.register({...})</c> at load time;
/// this descriptor only exists so the workbench's
/// <c>/api/ui/extensions</c> enumeration knows the extension is present
/// and which kinds it claims.
/// </para>
/// <para>
/// The <see cref="BundleResourceName"/>, <see cref="StylesResourceName"/>
/// and <see cref="AdditionalAssetNames"/> point at the vendored MapLibre
/// JS / CSS / LICENSE. The asset-serving endpoint resolves them from this
/// assembly's manifest-resource stream, never an external CDN — Bowire's
/// no-network guarantee survives a map mount when no
/// <c>Bowire:MapTileUrl</c> is configured.
/// </para>
/// <para>
/// Offline lockdown — when <c>Bowire:MapTileUrl</c> is unset, the widget
/// configures a no-source style with <b>no <c>glyphs</c> URL</b> and
/// <b>no <c>sprite</c> URL</b>. Selection / discriminator visuals are
/// rendered via MapLibre's circle-layer primitive (no <c>text-field</c>,
/// no symbol-layer icons) so no glyph PBF / sprite atlas request ever
/// leaves the process. A regex-over-bundle test pins this — any future
/// style tweak that re-introduces an external URL fails CI before the
/// no-network guarantee leaks.
/// </para>
/// </remarks>
[BowireExtension]
public sealed class MapLibreExtension : IBowireUiExtension
{
    /// <inheritdoc/>
    public string Id => "kuestenlogik.maplibre";

    /// <inheritdoc/>
    public string BowireApiRange => "1.x";

    /// <inheritdoc/>
    public IReadOnlyList<string> Kinds { get; } = ["coordinate.wgs84"];

    /// <inheritdoc/>
    public ExtensionCapabilities Capabilities
        => ExtensionCapabilities.Viewer | ExtensionCapabilities.Editor;

    /// <inheritdoc/>
    /// <remarks>
    /// Shipped as an embedded resource on the
    /// <c>Kuestenlogik.Bowire.Extension.MapLibre</c> assembly. Served to
    /// the workbench at <c>/api/ui/extensions/kuestenlogik.maplibre/map.js</c>
    /// — the workbench's extension loader dynamic-imports it on first
    /// mount of a <c>coordinate.wgs84</c> annotation.
    /// </remarks>
    public string BundleResourceName => "wwwroot/js/widgets/map.js";

    /// <inheritdoc/>
    public string? StylesResourceName => "wwwroot/maplibre/maplibre-gl.css";

    /// <summary>
    /// Extra asset names served under
    /// <c>/api/ui/extensions/{Id}/{name}</c>. The vendored MapLibre GL
    /// JS bundle itself rides here — the workbench fetches it lazily the
    /// first time a map widget is mounted, so the cost of the 800 KB
    /// renderer doesn't fall on every Bowire page load. The
    /// <c>LICENSE</c> file ships next to it to satisfy the BSD-3-Clause
    /// terms.
    /// </summary>
    public IReadOnlyList<string> AdditionalAssetNames { get; } =
    [
        "wwwroot/maplibre/maplibre-gl.js",
        "wwwroot/maplibre/LICENSE",
    ];
}
