// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Semantics;

namespace Kuestenlogik.Bowire.Tests.Mocking;

/// <summary>
/// Tests for <see cref="RecordingSchemaSnapshotBuilder"/> — the
/// effective-annotation-set snapshot that lands at the top of a Phase-5
/// recording file.
/// </summary>
public sealed class RecordingSchemaSnapshotBuilderTests
{
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

    [Fact]
    public void Builds_One_Entry_Per_Effective_Annotation_Matching_The_Filter()
    {
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "*", "$.position.lat"), BuiltInSemanticTags.CoordinateLatitude),
            (new AnnotationKey("svc", "m", "*", "$.position.lon"), BuiltInSemanticTags.CoordinateLongitude),
            (new AnnotationKey("other", "m", "*", "$.foo"), BuiltInSemanticTags.ImageBytes));

        var snapshot = RecordingSchemaSnapshotBuilder.Build(store,
            new[] { ("svc", "m") });

        Assert.Equal(2, snapshot.Annotations.Count);
        Assert.All(snapshot.Annotations, a => Assert.Equal("svc", a.Service));
    }

    [Fact]
    public void Empty_Filter_Captures_Every_Effective_Annotation()
    {
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "*", "$.position.lat"), BuiltInSemanticTags.CoordinateLatitude),
            (new AnnotationKey("other", "m", "*", "$.foo"), BuiltInSemanticTags.ImageBytes));

        var snapshot = RecordingSchemaSnapshotBuilder.Build(store, []);

        Assert.Equal(2, snapshot.Annotations.Count);
    }

    [Fact]
    public void Explicit_None_Suppression_Entries_Round_Trip()
    {
        // A user-tier "none" is a real annotation value — replay against
        // a different store still suppresses the lower-tier proposal.
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "*", "$.foo"), BuiltInSemanticTags.None));

        var snapshot = RecordingSchemaSnapshotBuilder.Build(store, [("svc", "m")]);

        Assert.Single(snapshot.Annotations);
        Assert.Equal("none", snapshot.Annotations[0].Semantic);
    }

    [Fact]
    public void Snapshot_Carries_Service_Method_MessageType_JsonPath_Semantic_For_Each_Entry()
    {
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "EntityStatePdu", "$.location.x"), BuiltInSemanticTags.CoordinateEcefX));

        var snapshot = RecordingSchemaSnapshotBuilder.Build(store, [("svc", "m")]);

        Assert.Single(snapshot.Annotations);
        var a = snapshot.Annotations[0];
        Assert.Equal("svc", a.Service);
        Assert.Equal("m", a.Method);
        Assert.Equal("EntityStatePdu", a.MessageType);
        Assert.Equal("$.location.x", a.JsonPath);
        Assert.Equal("coordinate.ecef.x", a.Semantic);
    }
}
