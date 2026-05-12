// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Semantics;
using Kuestenlogik.Bowire.Semantics.Detectors;

namespace Kuestenlogik.Bowire.Tests.Semantics.Detectors;

public sealed class Wgs84CoordinateDetectorTests
{
    private static readonly Wgs84CoordinateDetector s_detector = new();

    private static DetectionContext Ctx(string json)
        => new("svc", "m", AnnotationKey.Wildcard, JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Matches_Latitude_Longitude_Pair_At_Root()
    {
        var ctx = Ctx("""{"latitude": 53.5478, "longitude": 9.9925}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Key.JsonPath == "$.latitude" && r.Semantic == BuiltInSemanticTags.CoordinateLatitude);
        Assert.Contains(results, r => r.Key.JsonPath == "$.longitude" && r.Semantic == BuiltInSemanticTags.CoordinateLongitude);
    }

    [Fact]
    public void Matches_Short_Names_Lat_Lng()
    {
        var ctx = Ctx("""{"lat": 53.5, "lng": 9.9}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Key.JsonPath == "$.lat" && r.Semantic == BuiltInSemanticTags.CoordinateLatitude);
        Assert.Contains(results, r => r.Key.JsonPath == "$.lng" && r.Semantic == BuiltInSemanticTags.CoordinateLongitude);
    }

    [Fact]
    public void Matches_Lat_Long_Variant()
    {
        // "long" is one of the legal longitude spellings per the ADR regex.
        var ctx = Ctx("""{"lat": 53.5, "long": 9.9}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Key.JsonPath == "$.long" && r.Semantic == BuiltInSemanticTags.CoordinateLongitude);
    }

    [Fact]
    public void Matches_Lat_Lon_Variant()
    {
        // "lon" is the most common longitude spelling in real-world
        // APIs — GeoJSON-derived schemas almost always use it. Was a
        // deliberate gap in the original ADR regex; covered now.
        var ctx = Ctx("""{"lat": 53.5, "lon": 9.9}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Key.JsonPath == "$.lat" && r.Semantic == BuiltInSemanticTags.CoordinateLatitude);
        Assert.Contains(results, r => r.Key.JsonPath == "$.lon" && r.Semantic == BuiltInSemanticTags.CoordinateLongitude);
    }

    [Fact]
    public void Match_Is_Case_Insensitive()
    {
        var ctx = Ctx("""{"Latitude": 53.5, "LONGITUDE": 9.9}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Matches_Pair_At_Nested_Parent()
    {
        var ctx = Ctx("""{"position": {"lat": 53.5, "lng": 9.9}, "name": "Hamburg"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Key.JsonPath == "$.position.lat" && r.Semantic == BuiltInSemanticTags.CoordinateLatitude);
        Assert.Contains(results, r => r.Key.JsonPath == "$.position.lng" && r.Semantic == BuiltInSemanticTags.CoordinateLongitude);
    }

    [Fact]
    public void Does_Not_Match_Pair_Split_Across_Different_Parents()
    {
        // ADR's "Pair-without-parent" risk: lat under $.a, lng under $.b
        // means no pairing — user resolves manually.
        var ctx = Ctx("""{"a": {"lat": 53.5}, "b": {"lng": 9.9}}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Does_Not_Match_Unpaired_Lat_Alone()
    {
        var ctx = Ctx("""{"lat": 53.5, "name": "Hamburg"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Does_Not_Match_Unpaired_Lng_Alone()
    {
        var ctx = Ctx("""{"lng": 9.9, "name": "Hamburg"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Does_Not_Match_Pixel_Coordinates_With_X_And_Y_Names()
    {
        // pixelX / pixelY in the [-90, 90] / [-180, 180] range must NOT
        // be reinterpreted as WGS84. The name pattern is the gate.
        var ctx = Ctx("""{"pixelX": 5.2, "pixelY": 9.1}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Does_Not_Match_Generic_Xy_Pair()
    {
        // The classic false-positive shape from the ADR.
        var ctx = Ctx("""{"x": 5.2, "y": 9.1}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Rejects_Out_Of_Range_Latitude()
    {
        // 100 is outside [-90, 90].
        var ctx = Ctx("""{"lat": 100.0, "lng": 9.9}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Rejects_Out_Of_Range_Longitude()
    {
        var ctx = Ctx("""{"lat": 53.5, "lng": 200.0}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Rejects_Latency_Field_That_Starts_With_Lat()
    {
        // ADR explicitly calls out "latency" as a false-positive risk
        // — the anchored regex must not match it.
        var ctx = Ctx("""{"latency": 53.5, "lng": 9.9}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Two_Lat_Fields_At_Same_Parent_Are_Ambiguous_And_Skipped()
    {
        // Two latitude-shaped names at the same parent: we can't pair
        // them cleanly, the user resolves.
        var ctx = Ctx("""{"lat": 53.5, "latitude": 53.6, "lng": 9.9}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Carries_Service_Method_Discriminator_Through_Into_Keys()
    {
        var raw = JsonDocument.Parse("""{"lat": 53.5, "lng": 9.9}""");
        var ctx = new DetectionContext("dis.LiveExercise", "Subscribe", "EntityStatePdu", raw.RootElement);

        var results = s_detector.Detect(in ctx).ToList();

        Assert.All(results, r =>
        {
            Assert.Equal("dis.LiveExercise", r.Key.ServiceId);
            Assert.Equal("Subscribe", r.Key.MethodId);
            Assert.Equal("EntityStatePdu", r.Key.MessageType);
        });
    }

    [Fact]
    public void Walks_Into_Array_Elements_And_Detects_Per_Object()
    {
        var ctx = Ctx("""{"ships": [{"lat": 53.5, "lng": 9.9}, {"lat": 54.0, "lng": 10.0}]}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Equal(4, results.Count);
        Assert.Contains(results, r => r.Key.JsonPath == "$.ships[0].lat");
        Assert.Contains(results, r => r.Key.JsonPath == "$.ships[0].lng");
        Assert.Contains(results, r => r.Key.JsonPath == "$.ships[1].lat");
        Assert.Contains(results, r => r.Key.JsonPath == "$.ships[1].lng");
    }
}
