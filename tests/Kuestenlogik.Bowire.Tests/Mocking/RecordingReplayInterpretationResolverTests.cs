// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Semantics;
using Kuestenlogik.Bowire.Semantics.Detectors;

namespace Kuestenlogik.Bowire.Tests.Mocking;

/// <summary>
/// Tests for <see cref="RecordingReplayInterpretationResolver"/> — the
/// Phase-5 short-circuit that decides whether to honour captured
/// interpretations or fall back to the live prober + builder pass.
/// </summary>
/// <remarks>
/// <para>
/// The counting <see cref="TestDoubleProber"/> is the test seam the
/// ADR pins for verifying determinism: a v2 recording (interpretations
/// captured) must never tick the counter on replay, regardless of how
/// many frames flow through the resolver.
/// </para>
/// </remarks>
public sealed class RecordingReplayInterpretationResolverTests
{
    private sealed class TestDoubleProber : IFrameProber
    {
        public int ObserveCalls;
        public void ObserveFrame(in DetectionContext ctx) => ObserveCalls++;
    }

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

    [Fact]
    public void Captured_Interpretations_Short_Circuit_The_Prober()
    {
        // v2 recording: the step carries captured interpretations from
        // record-time, so replay should NOT invoke the prober.
        var captured = new List<RecordedInterpretation>
        {
            new("coordinate.wgs84", "$.position",
                JsonSerializer.SerializeToElement(new { lat = 53.5, lon = 9.9 })),
        };

        var prober = new TestDoubleProber();
        var store = StoreWith();
        var frame = Frame("""{"position": {"lat": 99.0, "lon": 88.0}}""");

        var result = RecordingReplayInterpretationResolver.Resolve(
            capturedInterpretations: captured,
            prober: prober,
            store: store,
            serviceId: "svc",
            methodId: "m",
            messageType: "*",
            frame: frame);

        Assert.Same(captured, result);
        Assert.Equal(0, prober.ObserveCalls);
    }

    [Fact]
    public void Pre_Phase5_Recording_Falls_Back_To_Live_Detection()
    {
        // v1 recording: the step has no interpretations field, so replay
        // hands the frame to the prober + builder as a fallback.
        var prober = new TestDoubleProber();
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "*", "$.position.lat"), BuiltInSemanticTags.CoordinateLatitude),
            (new AnnotationKey("svc", "m", "*", "$.position.lon"), BuiltInSemanticTags.CoordinateLongitude));
        var frame = Frame("""{"position": {"lat": 53.5, "lon": 9.9}}""");

        var result = RecordingReplayInterpretationResolver.Resolve(
            capturedInterpretations: null,
            prober: prober,
            store: store,
            serviceId: "svc",
            methodId: "m",
            messageType: "*",
            frame: frame);

        Assert.Equal(1, prober.ObserveCalls);
        Assert.Single(result);
        Assert.Equal("coordinate.wgs84", result[0].Kind);
    }

    [Fact]
    public void Captured_Empty_Interpretations_Still_Short_Circuit()
    {
        // A captured-but-empty list is still a deliberate "nothing
        // interpretable" signal, NOT a "we didn't bother capturing"
        // signal. The resolver honours it without falling back.
        var captured = new List<RecordedInterpretation>();
        var prober = new TestDoubleProber();
        // Store with annotations that WOULD match the frame — proving
        // the resolver doesn't sneakily run the builder anyway.
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "*", "$.position.lat"), BuiltInSemanticTags.CoordinateLatitude),
            (new AnnotationKey("svc", "m", "*", "$.position.lon"), BuiltInSemanticTags.CoordinateLongitude));
        var frame = Frame("""{"position": {"lat": 53.5, "lon": 9.9}}""");

        var result = RecordingReplayInterpretationResolver.Resolve(
            capturedInterpretations: captured,
            prober: prober,
            store: store,
            serviceId: "svc",
            methodId: "m",
            messageType: "*",
            frame: frame);

        Assert.Empty(result);
        Assert.Equal(0, prober.ObserveCalls);
    }

    [Fact]
    public void Pre_Phase5_Replay_Without_Store_Returns_Empty_List()
    {
        // No store wired (e.g. AddBowire wasn't called) → the fall-back
        // path can't resolve anything, returns an empty list rather
        // than throwing.
        var prober = new TestDoubleProber();
        var frame = Frame("""{"position": {"lat": 53.5, "lon": 9.9}}""");

        var result = RecordingReplayInterpretationResolver.Resolve(
            capturedInterpretations: null,
            prober: prober,
            store: null,
            serviceId: "svc",
            methodId: "m",
            messageType: "*",
            frame: frame);

        Assert.Empty(result);
        Assert.Equal(0, prober.ObserveCalls);
    }

    [Fact]
    public void Pre_Phase5_Replay_Without_Prober_Still_Runs_The_Builder()
    {
        // No prober (DisableBuiltInDetectors opted in) — the fall-back
        // path still resolves interpretations from the existing
        // annotation store, just without seeding the auto-detector layer.
        var store = StoreWith(
            (new AnnotationKey("svc", "m", "*", "$.position.lat"), BuiltInSemanticTags.CoordinateLatitude),
            (new AnnotationKey("svc", "m", "*", "$.position.lon"), BuiltInSemanticTags.CoordinateLongitude));
        var frame = Frame("""{"position": {"lat": 53.5, "lon": 9.9}}""");

        var result = RecordingReplayInterpretationResolver.Resolve(
            capturedInterpretations: null,
            prober: null,
            store: store,
            serviceId: "svc",
            methodId: "m",
            messageType: "*",
            frame: frame);

        Assert.Single(result);
    }
}
