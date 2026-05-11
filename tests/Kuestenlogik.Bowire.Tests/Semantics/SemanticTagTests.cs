// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Semantics;

namespace Kuestenlogik.Bowire.Tests.Semantics;

public sealed class SemanticTagTests
{
    [Fact]
    public void Construction_Stores_Kind_Verbatim()
    {
        var tag = new SemanticTag("coordinate.latitude");
        Assert.Equal("coordinate.latitude", tag.Kind);
    }

    [Fact]
    public void ToString_Returns_Kind()
    {
        var tag = new SemanticTag("image.bytes");
        Assert.Equal("image.bytes", tag.ToString());
    }

    [Fact]
    public void IsNone_Matches_None_Kind_String()
    {
        Assert.True(new SemanticTag("none").IsNone);
        Assert.True(BuiltInSemanticTags.None.IsNone);
    }

    [Fact]
    public void IsNone_Is_False_For_Any_Other_Tag()
    {
        Assert.False(new SemanticTag("coordinate.latitude").IsNone);
        Assert.False(new SemanticTag("None").IsNone, "case-sensitive");
        Assert.False(new SemanticTag("").IsNone);
    }

    [Fact]
    public void Equality_Is_Value_Based()
        => Assert.Equal(new SemanticTag("x"), new SemanticTag("x"));

    [Fact]
    public void BuiltIn_Constants_Match_Documented_Kind_Strings()
    {
        // Every kind the ADR pins as a built-in must be reachable
        // through BuiltInSemanticTags by the exact string the ADR
        // documents. The frame-semantics-framework.md "Annotation
        // value (semantic)" section is the source of truth.
        Assert.Equal("none", BuiltInSemanticTags.None.Kind);
        Assert.Equal("coordinate.latitude", BuiltInSemanticTags.CoordinateLatitude.Kind);
        Assert.Equal("coordinate.longitude", BuiltInSemanticTags.CoordinateLongitude.Kind);
        Assert.Equal("coordinate.ecef.x", BuiltInSemanticTags.CoordinateEcefX.Kind);
        Assert.Equal("coordinate.ecef.y", BuiltInSemanticTags.CoordinateEcefY.Kind);
        Assert.Equal("coordinate.ecef.z", BuiltInSemanticTags.CoordinateEcefZ.Kind);
        Assert.Equal("image.bytes", BuiltInSemanticTags.ImageBytes.Kind);
        Assert.Equal("image.mime-type", BuiltInSemanticTags.ImageMimeType.Kind);
        Assert.Equal("audio.bytes", BuiltInSemanticTags.AudioBytes.Kind);
        Assert.Equal("audio.sample-rate", BuiltInSemanticTags.AudioSampleRate.Kind);
        Assert.Equal("timeseries.timestamp", BuiltInSemanticTags.TimeseriesTimestamp.Kind);
        Assert.Equal("timeseries.value", BuiltInSemanticTags.TimeseriesValue.Kind);
        Assert.Equal("table.row-array", BuiltInSemanticTags.TableRowArray.Kind);
    }
}
