// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Semantics;

namespace Kuestenlogik.Bowire.Tests.Mocking;

/// <summary>
/// Tests for the Phase-5 <see cref="RecordingInterpretationBuilder"/> —
/// pure-function helper that resolves the effective annotation set + a
/// captured frame into <see cref="RecordedInterpretation"/> entries.
/// </summary>
public sealed class RecordingInterpretationBuilderTests
{
    private static JsonElement Frame(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static LayeredAnnotationStore StoreWith(
        params (AnnotationKey Key, SemanticTag Tag)[] entries)
    {
        var user = new InMemoryAnnotationLayer();
        foreach (var (k, t) in entries) user.Set(k, t);
        return new LayeredAnnotationStore(
            userSessionLayer: user,
            userFileLayer: null,
            projectFileLayer: null,
            autoDetectorLayer: new InMemoryAnnotationLayer(),
            pluginHints: (_, _) => []);
    }

    // -----------------------------------------------------------------
    // WGS84 coordinate pairing — the workhorse case.
    // -----------------------------------------------------------------

    [Fact]
    public void Paired_Lat_Lon_Annotations_Emit_One_Coordinate_Wgs84_Interpretation()
    {
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "*", "$.position.lat"), BuiltInSemanticTags.CoordinateLatitude),
            (new AnnotationKey("svc", "m", "*", "$.position.lon"), BuiltInSemanticTags.CoordinateLongitude));

        var frame = Frame("""{"position": {"lat": 53.5478, "lon": 9.9925}}""");
        var results = RecordingInterpretationBuilder.Build(store, "svc", "m", "*", frame);

        Assert.Single(results);
        Assert.Equal("coordinate.wgs84", results[0].Kind);
        Assert.Equal("$.position", results[0].Path);

        var payload = results[0].Payload;
        Assert.Equal(53.5478, payload.GetProperty("lat").GetDouble());
        Assert.Equal(9.9925, payload.GetProperty("lon").GetDouble());
        Assert.Equal("$.position.lat", payload.GetProperty("latPath").GetString());
        Assert.Equal("$.position.lon", payload.GetProperty("lonPath").GetString());
    }

    [Fact]
    public void Lat_Without_Companion_Lon_Emits_Nothing()
    {
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "*", "$.position.lat"), BuiltInSemanticTags.CoordinateLatitude));

        var frame = Frame("""{"position": {"lat": 53.5478, "lon": 9.9925}}""");
        var results = RecordingInterpretationBuilder.Build(store, "svc", "m", "*", frame);

        Assert.Empty(results);
    }

    [Fact]
    public void Coordinate_Pair_Where_Frame_Is_Missing_The_Field_Skips_Silently()
    {
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "*", "$.position.lat"), BuiltInSemanticTags.CoordinateLatitude),
            (new AnnotationKey("svc", "m", "*", "$.position.lon"), BuiltInSemanticTags.CoordinateLongitude));

        // lat is present, lon is missing → can't pair, drop the interpretation
        var frame = Frame("""{"position": {"lat": 53.5478}}""");
        var results = RecordingInterpretationBuilder.Build(store, "svc", "m", "*", frame);

        Assert.Empty(results);
    }

    [Fact]
    public void Explicit_None_Suppression_Skips_The_Annotation()
    {
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "*", "$.position.lat"), BuiltInSemanticTags.None),
            (new AnnotationKey("svc", "m", "*", "$.position.lon"), BuiltInSemanticTags.CoordinateLongitude));

        var frame = Frame("""{"position": {"lat": 53.5478, "lon": 9.9925}}""");
        var results = RecordingInterpretationBuilder.Build(store, "svc", "m", "*", frame);

        // lat is suppressed, so there's no lat/lon pair → no interpretation
        Assert.Empty(results);
    }

    // -----------------------------------------------------------------
    // Discriminator matching — wildcard + concrete.
    // -----------------------------------------------------------------

    [Fact]
    public void Wildcard_Annotations_Match_Every_Concrete_Discriminator()
    {
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "*", "$.position.lat"), BuiltInSemanticTags.CoordinateLatitude),
            (new AnnotationKey("svc", "m", "*", "$.position.lon"), BuiltInSemanticTags.CoordinateLongitude));

        var frame = Frame("""{"position": {"lat": 53.5478, "lon": 9.9925}}""");
        var results = RecordingInterpretationBuilder.Build(store, "svc", "m", "EntityStatePdu", frame);

        Assert.Single(results);
    }

    [Fact]
    public void Concrete_Discriminator_Does_Not_Match_Other_Concrete_Discriminators()
    {
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "EntityStatePdu", "$.position.lat"), BuiltInSemanticTags.CoordinateLatitude),
            (new AnnotationKey("svc", "m", "EntityStatePdu", "$.position.lon"), BuiltInSemanticTags.CoordinateLongitude));

        var frame = Frame("""{"position": {"lat": 53.5478, "lon": 9.9925}}""");
        var results = RecordingInterpretationBuilder.Build(store, "svc", "m", "FirePdu", frame);

        Assert.Empty(results);
    }

    // -----------------------------------------------------------------
    // Image bytes — with and without mime companion.
    // -----------------------------------------------------------------

    [Fact]
    public void Image_Bytes_Without_Mime_Companion_Emits_Only_Bytes()
    {
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "*", "$.preview"), BuiltInSemanticTags.ImageBytes));

        var frame = Frame("""{"preview": "iVBORw0KGgo="}""");
        var results = RecordingInterpretationBuilder.Build(store, "svc", "m", "*", frame);

        Assert.Single(results);
        Assert.Equal(BuiltInSemanticTags.ImageBytes.Kind, results[0].Kind);
        Assert.Equal("$", results[0].Path);
        Assert.Equal("iVBORw0KGgo=", results[0].Payload.GetProperty("data").GetString());
        Assert.False(results[0].Payload.TryGetProperty("mimeType", out _));
    }

    [Fact]
    public void Image_Bytes_With_Mime_Companion_Inlines_Both()
    {
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "*", "$.image.bytes"), BuiltInSemanticTags.ImageBytes),
            (new AnnotationKey("svc", "m", "*", "$.image.contentType"), BuiltInSemanticTags.ImageMimeType));

        var frame = Frame("""{"image": {"bytes": "iVBORw0KGgo=", "contentType": "image/png"}}""");
        var results = RecordingInterpretationBuilder.Build(store, "svc", "m", "*", frame);

        Assert.Single(results);
        Assert.Equal("$.image", results[0].Path);
        Assert.Equal("iVBORw0KGgo=", results[0].Payload.GetProperty("data").GetString());
        Assert.Equal("image/png", results[0].Payload.GetProperty("mimeType").GetString());
    }

    // -----------------------------------------------------------------
    // Audio bytes — same shape, with optional sample-rate companion.
    // -----------------------------------------------------------------

    [Fact]
    public void Audio_Bytes_With_Sample_Rate_Companion_Inlines_Both()
    {
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "*", "$.clip.bytes"), BuiltInSemanticTags.AudioBytes),
            (new AnnotationKey("svc", "m", "*", "$.clip.sampleRate"), BuiltInSemanticTags.AudioSampleRate));

        var frame = Frame("""{"clip": {"bytes": "T2dnUw==", "sampleRate": 44100}}""");
        var results = RecordingInterpretationBuilder.Build(store, "svc", "m", "*", frame);

        Assert.Single(results);
        Assert.Equal(BuiltInSemanticTags.AudioBytes.Kind, results[0].Kind);
        Assert.Equal("$.clip", results[0].Path);
        Assert.Equal("T2dnUw==", results[0].Payload.GetProperty("data").GetString());
        Assert.Equal(44100, results[0].Payload.GetProperty("sampleRate").GetDouble());
    }

    // -----------------------------------------------------------------
    // Service / method scoping.
    // -----------------------------------------------------------------

    [Fact]
    public void Annotations_On_Other_Service_Are_Ignored()
    {
        var store = StoreWith(
            (new AnnotationKey("other", "m", "*", "$.lat"), BuiltInSemanticTags.CoordinateLatitude),
            (new AnnotationKey("other", "m", "*", "$.lon"), BuiltInSemanticTags.CoordinateLongitude));

        var frame = Frame("""{"lat": 53.5, "lon": 9.9}""");
        var results = RecordingInterpretationBuilder.Build(store, "svc", "m", "*", frame);

        Assert.Empty(results);
    }

    [Fact]
    public void Empty_Store_Returns_Empty_List_Without_Throwing()
    {
        var store = StoreWith();
        var frame = Frame("""{"position": {"lat": 53.5, "lon": 9.9}}""");
        var results = RecordingInterpretationBuilder.Build(store, "svc", "m", "*", frame);

        Assert.NotNull(results);
        Assert.Empty(results);
    }

    // -----------------------------------------------------------------
    // ParentPathOf helper — the grouping primitive.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("$.position.lat", "$.position")]
    [InlineData("$.lat", "$")]
    [InlineData("$.items[3].lat", "$.items[3]")]
    [InlineData("$", "$")]
    [InlineData("", "$")]
    public void ParentPathOf_Strips_The_Last_Segment(string input, string expected)
    {
        Assert.Equal(expected, RecordingInterpretationBuilder.ParentPathOf(input));
    }

    // -----------------------------------------------------------------
    // TryResolve — minimal JSONPath resolver.
    // -----------------------------------------------------------------

    [Fact]
    public void TryResolve_Walks_Nested_Objects()
    {
        var frame = Frame("""{"a": {"b": {"c": 42}}}""");
        var ok = RecordingInterpretationBuilder.TryResolve(frame, "$.a.b.c", out var leaf);

        Assert.True(ok);
        Assert.Equal(42, leaf.GetInt32());
    }

    [Fact]
    public void TryResolve_Walks_Bracket_Indexed_Arrays()
    {
        var frame = Frame("""{"items": [{"name": "a"}, {"name": "b"}]}""");
        var ok = RecordingInterpretationBuilder.TryResolve(frame, "$.items[1].name", out var leaf);

        Assert.True(ok);
        Assert.Equal("b", leaf.GetString());
    }

    [Fact]
    public void TryResolve_Returns_False_On_Missing_Property()
    {
        var frame = Frame("""{"a": {"b": 1}}""");
        var ok = RecordingInterpretationBuilder.TryResolve(frame, "$.a.x", out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryResolve_Returns_False_On_Out_Of_Bounds_Index()
    {
        var frame = Frame("""{"items": [{"name": "a"}]}""");
        var ok = RecordingInterpretationBuilder.TryResolve(frame, "$.items[5].name", out _);
        Assert.False(ok);
    }
}
