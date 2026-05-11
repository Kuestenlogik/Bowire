// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Semantics;

namespace Kuestenlogik.Bowire.Tests.Semantics;

public sealed class InMemoryAnnotationLayerTests
{
    private static readonly AnnotationKey s_key = AnnotationKey.ForSingleType("svc", "m", "$.x");

    [Fact]
    public void Set_Then_Get_Returns_The_Tag()
    {
        var layer = new InMemoryAnnotationLayer();
        layer.Set(s_key, BuiltInSemanticTags.CoordinateLatitude);
        Assert.Equal(BuiltInSemanticTags.CoordinateLatitude, layer.Get(s_key));
        Assert.Equal(1, layer.Count);
    }

    [Fact]
    public void Get_Returns_Null_For_Missing_Key()
    {
        var layer = new InMemoryAnnotationLayer();
        Assert.Null(layer.Get(s_key));
    }

    [Fact]
    public void Set_Replaces_Existing_Tag()
    {
        var layer = new InMemoryAnnotationLayer();
        layer.Set(s_key, BuiltInSemanticTags.CoordinateLatitude);
        layer.Set(s_key, BuiltInSemanticTags.CoordinateLongitude);
        Assert.Equal(BuiltInSemanticTags.CoordinateLongitude, layer.Get(s_key));
        Assert.Equal(1, layer.Count);
    }

    [Fact]
    public void Remove_Returns_True_When_Entry_Existed()
    {
        var layer = new InMemoryAnnotationLayer();
        layer.Set(s_key, BuiltInSemanticTags.CoordinateLatitude);
        Assert.True(layer.Remove(s_key));
        Assert.Null(layer.Get(s_key));
        Assert.Equal(0, layer.Count);
    }

    [Fact]
    public void Remove_Returns_False_When_Key_Was_Absent()
    {
        var layer = new InMemoryAnnotationLayer();
        Assert.False(layer.Remove(s_key));
    }

    [Fact]
    public void Clear_Drops_Every_Entry()
    {
        var layer = new InMemoryAnnotationLayer();
        layer.Set(s_key, BuiltInSemanticTags.CoordinateLatitude);
        layer.Set(AnnotationKey.ForSingleType("svc", "m", "$.y"), BuiltInSemanticTags.CoordinateLongitude);

        layer.Clear();

        Assert.Equal(0, layer.Count);
    }

    [Fact]
    public void Snapshot_Reflects_Current_Entries()
    {
        var layer = new InMemoryAnnotationLayer();
        layer.Set(s_key, BuiltInSemanticTags.CoordinateLatitude);

        var snapshot = layer.Snapshot();

        Assert.Single(snapshot);
        Assert.Equal(s_key, snapshot.First().Key);
        Assert.Equal(BuiltInSemanticTags.CoordinateLatitude, snapshot.First().Value);
    }

    [Fact]
    public void Snapshot_Is_Detached_From_Live_Dictionary()
    {
        var layer = new InMemoryAnnotationLayer();
        layer.Set(s_key, BuiltInSemanticTags.CoordinateLatitude);

        var snapshot = layer.Snapshot();
        // Concurrent mutation must not throw mid-enumeration.
        layer.Set(AnnotationKey.ForSingleType("svc", "m", "$.y"), BuiltInSemanticTags.CoordinateLongitude);
        layer.Remove(s_key);

        Assert.Single(snapshot, kvp => kvp.Key == s_key);
    }
}
