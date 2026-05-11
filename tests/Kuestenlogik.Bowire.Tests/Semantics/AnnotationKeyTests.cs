// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Semantics;

namespace Kuestenlogik.Bowire.Tests.Semantics;

public sealed class AnnotationKeyTests
{
    [Fact]
    public void Constructor_Captures_All_Four_Dimensions()
    {
        var key = new AnnotationKey("dis.LiveExercise", "Subscribe", "EntityStatePdu", "$.entityLocation.x");

        Assert.Equal("dis.LiveExercise", key.ServiceId);
        Assert.Equal("Subscribe", key.MethodId);
        Assert.Equal("EntityStatePdu", key.MessageType);
        Assert.Equal("$.entityLocation.x", key.JsonPath);
    }

    [Fact]
    public void ForSingleType_Uses_Wildcard_For_MessageType()
    {
        var key = AnnotationKey.ForSingleType("harbor.HarborService", "WatchCrane", "$.position.lat");

        Assert.Equal("*", key.MessageType);
        Assert.Equal(AnnotationKey.Wildcard, key.MessageType);
    }

    [Fact]
    public void Equality_Is_Value_Based()
    {
        var a = new AnnotationKey("s", "m", "*", "$.x");
        var b = new AnnotationKey("s", "m", "*", "$.x");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Differing_Discriminator_Produces_Distinct_Keys()
    {
        // Same service/method/path, different discriminator value —
        // exactly the case the four-dimensional key exists to handle.
        var a = new AnnotationKey("dis", "Subscribe", "EntityStatePdu", "$.location.x");
        var b = new AnnotationKey("dis", "Subscribe", "FirePdu", "$.location.x");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ServiceId_Is_Case_Sensitive()
    {
        var lower = new AnnotationKey("dis", "Subscribe", "*", "$.x");
        var upper = new AnnotationKey("DIS", "Subscribe", "*", "$.x");

        Assert.NotEqual(lower, upper);
    }

    [Fact]
    public void Wildcard_Constant_Is_The_Literal_Asterisk()
        => Assert.Equal("*", AnnotationKey.Wildcard);
}
