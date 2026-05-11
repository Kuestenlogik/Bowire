// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Semantics.Extensions;

namespace Kuestenlogik.Bowire.Tests.Semantics.Endpoints;

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
