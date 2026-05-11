// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Semantics;

namespace Kuestenlogik.Bowire.Tests.Semantics;

public sealed class AnnotationTests
{
    [Fact]
    public void Construction_Captures_Key_Tag_And_Source()
    {
        var key = AnnotationKey.ForSingleType("svc", "m", "$.x");
        var ann = new Annotation(key, BuiltInSemanticTags.CoordinateLatitude, AnnotationSource.Plugin);

        Assert.Equal(key, ann.Key);
        Assert.Equal(BuiltInSemanticTags.CoordinateLatitude, ann.Semantic);
        Assert.Equal(AnnotationSource.Plugin, ann.Source);
    }

    [Fact]
    public void Equality_Is_Value_Based_Across_All_Three_Fields()
    {
        var key = AnnotationKey.ForSingleType("svc", "m", "$.x");
        var a = new Annotation(key, BuiltInSemanticTags.CoordinateLatitude, AnnotationSource.User);
        var b = new Annotation(key, BuiltInSemanticTags.CoordinateLatitude, AnnotationSource.User);
        var c = new Annotation(key, BuiltInSemanticTags.CoordinateLatitude, AnnotationSource.Auto);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void AnnotationSource_Priority_Is_User_Plugin_Auto_None()
    {
        // The enum is ordered low-to-high so a numeric comparison gives
        // the priority answer directly. Two consumers of this fact are
        // the resolver and the source-badge UI.
        Assert.True((int)AnnotationSource.User > (int)AnnotationSource.Plugin);
        Assert.True((int)AnnotationSource.Plugin > (int)AnnotationSource.Auto);
        Assert.True((int)AnnotationSource.Auto > (int)AnnotationSource.None);
    }
}
