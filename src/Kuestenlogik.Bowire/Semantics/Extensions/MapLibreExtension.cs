// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics.Extensions;

/// <summary>
/// Built-in Bowire UI extension that mounts the MapLibre GL JS map widget
/// on the <c>coordinate.wgs84</c> semantic kind. Phase 3 of the
/// frame-semantics framework — the first concrete consumer of the
/// extension API the ADR pins under "Extension framework".
/// </summary>
/// <remarks>
/// <para>
/// The descriptor only carries metadata + resource pointers; all rendering
/// logic lives in the JS bundle at <c>wwwroot/js/widgets/map.js</c>,
/// concatenated into the bowire.js fragment chain by the
/// <c>ConcatBowireJs</c> MSBuild target. The bundle calls
/// <c>window.BowireExtensions.register({...})</c> at load time; this
/// descriptor only exists so the workbench's <c>/api/ui/extensions</c>
/// enumeration knows the extension is present and which kinds it claims.
/// </para>
/// <para>
/// The <see cref="BundleResourceName"/> and
/// <see cref="StylesResourceName"/> point at the MapLibre vendored
/// stylesheet shipped under <c>wwwroot/maplibre/</c> — those files are
/// served at <c>/api/ui/extensions/kuestenlogik.maplibre/{name}</c>
/// so the JS bundle can dynamic-import them without an external CDN
/// reference, honouring Bowire's offline-safe guarantee.
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
    /// The actual <c>register({...})</c> call lives inside the concat'd
    /// bowire.js fragment, but the asset endpoint still serves the file
    /// directly for tooling / debugging / future dynamic-import flows.
    /// </remarks>
    public string BundleResourceName => "wwwroot/js/widgets/map.js";

    /// <inheritdoc/>
    public string? StylesResourceName => "wwwroot/maplibre/maplibre-gl.css";

    /// <summary>
    /// Extra asset names served under
    /// <c>/api/ui/extensions/{Id}/{name}</c>. The MapLibre bundle itself
    /// is shipped as one of these — the workbench fetches it lazily the
    /// first time a map widget is mounted, so the cost of the 800 KB GL
    /// renderer doesn't fall on every Bowire page load.
    /// </summary>
    public IReadOnlyList<string> AdditionalAssetNames { get; } =
    [
        "wwwroot/maplibre/maplibre-gl.js",
        "wwwroot/maplibre/LICENSE",
    ];
}
