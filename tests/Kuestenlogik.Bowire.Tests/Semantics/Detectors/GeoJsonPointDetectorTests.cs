// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Semantics;
using Kuestenlogik.Bowire.Semantics.Detectors;

namespace Kuestenlogik.Bowire.Tests.Semantics.Detectors;

public sealed class GeoJsonPointDetectorTests
{
    private static readonly GeoJsonPointDetector s_detector = new();

    private static DetectionContext Ctx(string json)
        => new("svc", "m", AnnotationKey.Wildcard, JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Matches_Bare_Geojson_Point()
    {
        // GeoJSON pins lon-first ordering — coordinates[0] is longitude.
        var ctx = Ctx("""{"type": "Point", "coordinates": [9.9925, 53.5478]}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Key.JsonPath == "$.coordinates[0]" && r.Semantic == BuiltInSemanticTags.CoordinateLongitude);
        Assert.Contains(results, r => r.Key.JsonPath == "$.coordinates[1]" && r.Semantic == BuiltInSemanticTags.CoordinateLatitude);
    }

    [Fact]
    public void Matches_Nested_Geojson_Point()
    {
        var ctx = Ctx("""{"ship": "ESPERANZA", "geometry": {"type": "Point", "coordinates": [9.9925, 53.5478]}}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Key.JsonPath == "$.geometry.coordinates[0]" && r.Semantic == BuiltInSemanticTags.CoordinateLongitude);
        Assert.Contains(results, r => r.Key.JsonPath == "$.geometry.coordinates[1]" && r.Semantic == BuiltInSemanticTags.CoordinateLatitude);
    }

    [Fact]
    public void Accepts_3D_Coordinates()
    {
        // 3-element: lon, lat, altitude. We only annotate lon and lat;
        // altitude is left alone (no built-in tag for it in v1).
        var ctx = Ctx("""{"type": "Point", "coordinates": [9.9925, 53.5478, 12.0]}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, r => r.Key.JsonPath == "$.coordinates[2]");
    }

    [Fact]
    public void Does_Not_Match_When_Type_Is_Wrong()
    {
        var ctx = Ctx("""{"type": "Polygon", "coordinates": [9.9925, 53.5478]}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Does_Not_Match_When_Type_Is_Missing()
    {
        var ctx = Ctx("""{"coordinates": [9.9925, 53.5478]}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Does_Not_Match_When_Coordinates_Is_Wrong_Length()
    {
        var single = Ctx("""{"type": "Point", "coordinates": [9.9925]}""");
        var four = Ctx("""{"type": "Point", "coordinates": [9.9925, 53.5478, 12.0, 999.0]}""");

        Assert.Empty(s_detector.Detect(in single));
        Assert.Empty(s_detector.Detect(in four));
    }

    [Fact]
    public void Does_Not_Match_When_Coordinates_Contains_Non_Numbers()
    {
        var ctx = Ctx("""{"type": "Point", "coordinates": ["9.9925", "53.5478"]}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Does_Not_Match_When_Coordinates_Range_Is_Wrong()
    {
        // Longitude 200° is out-of-range; 2D pixel coords named
        // "coordinates" inside a "Point" object should not fire.
        var ctx = Ctx("""{"type": "Point", "coordinates": [200.0, 53.5]}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Does_Not_Match_Geometry_Collection_Member_Without_Type_Point()
    {
        // Multi-feature payload; only the Point branch fires.
        var ctx = Ctx("""
            {
              "features": [
                {"type": "Point", "coordinates": [9.9, 53.5]},
                {"type": "LineString", "coordinates": [[1, 2], [3, 4]]}
              ]
            }
            """);

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Key.JsonPath == "$.features[0].coordinates[0]");
        Assert.Contains(results, r => r.Key.JsonPath == "$.features[0].coordinates[1]");
    }

    [Fact]
    public void Carries_Discriminator_Through_Into_Keys()
    {
        var raw = JsonDocument.Parse("""{"type": "Point", "coordinates": [9.9, 53.5]}""");
        var ctx = new DetectionContext("svc", "m", "PositionUpdate", raw.RootElement);

        var results = s_detector.Detect(in ctx).ToList();

        Assert.All(results, r => Assert.Equal("PositionUpdate", r.Key.MessageType));
    }
}
