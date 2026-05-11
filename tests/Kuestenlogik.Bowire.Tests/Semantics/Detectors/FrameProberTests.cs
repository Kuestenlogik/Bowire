// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Semantics;
using Kuestenlogik.Bowire.Semantics.Detectors;

namespace Kuestenlogik.Bowire.Tests.Semantics.Detectors;

public sealed class FrameProberTests
{
    private static DetectionContext Ctx(string svc, string mth, string disc, string json)
        => new(svc, mth, disc, JsonDocument.Parse(json).RootElement);

    private sealed class CountingDetector : IBowireFieldDetector
    {
        public int Calls;
        public string Id => "test.counting";
        public List<DetectionResult> Results { get; } = [];

        public IEnumerable<DetectionResult> Detect(in DetectionContext ctx)
        {
            Calls++;
            return Results;
        }
    }

    private sealed class ThrowingDetector : IBowireFieldDetector
    {
        public string Id => "test.throwing";

        public IEnumerable<DetectionResult> Detect(in DetectionContext ctx)
            => throw new InvalidOperationException("kaboom");
    }

    [Fact]
    public void First_Observe_Of_A_Triple_Runs_Every_Detector()
    {
        var d1 = new CountingDetector();
        var d2 = new CountingDetector();
        var autoLayer = new InMemoryAnnotationLayer();
        var prober = new FrameProber([d1, d2], autoLayer);

        var ctx = Ctx("svc", "m", "*", """{"a": 1}""");
        prober.ObserveFrame(in ctx);

        Assert.Equal(1, d1.Calls);
        Assert.Equal(1, d2.Calls);
        Assert.Equal(1, prober.ProbedTripleCount);
    }

    [Fact]
    public void Repeated_Observe_Of_Same_Triple_Does_Not_Re_Run_Detectors()
    {
        var d = new CountingDetector();
        var prober = new FrameProber([d], new InMemoryAnnotationLayer());

        var ctxA = Ctx("svc", "m", "*", """{"a": 1}""");
        var ctxB = Ctx("svc", "m", "*", """{"a": 99, "b": "different frame"}""");

        prober.ObserveFrame(in ctxA);
        prober.ObserveFrame(in ctxB);
        prober.ObserveFrame(in ctxB);

        Assert.Equal(1, d.Calls);
        Assert.Equal(1, prober.ProbedTripleCount);
    }

    [Fact]
    public void Different_MessageType_Triggers_Fresh_Probe()
    {
        var d = new CountingDetector();
        var prober = new FrameProber([d], new InMemoryAnnotationLayer());

        prober.ObserveFrame(Ctx("svc", "m", "EntityStatePdu", """{"a": 1}"""));
        prober.ObserveFrame(Ctx("svc", "m", "FirePdu", """{"b": 2}"""));

        Assert.Equal(2, d.Calls);
        Assert.Equal(2, prober.ProbedTripleCount);
    }

    [Fact]
    public void Different_Method_Triggers_Fresh_Probe()
    {
        var d = new CountingDetector();
        var prober = new FrameProber([d], new InMemoryAnnotationLayer());

        prober.ObserveFrame(Ctx("svc", "m1", "*", """{"a": 1}"""));
        prober.ObserveFrame(Ctx("svc", "m2", "*", """{"a": 2}"""));

        Assert.Equal(2, d.Calls);
    }

    [Fact]
    public void Different_Service_Triggers_Fresh_Probe()
    {
        var d = new CountingDetector();
        var prober = new FrameProber([d], new InMemoryAnnotationLayer());

        prober.ObserveFrame(Ctx("s1", "m", "*", """{"a": 1}"""));
        prober.ObserveFrame(Ctx("s2", "m", "*", """{"a": 2}"""));

        Assert.Equal(2, d.Calls);
    }

    [Fact]
    public void Detector_Results_Land_In_The_Auto_Layer()
    {
        var autoLayer = new InMemoryAnnotationLayer();
        var d = new CountingDetector();
        d.Results.Add(new DetectionResult(
            new AnnotationKey("svc", "m", "*", "$.lat"),
            BuiltInSemanticTags.CoordinateLatitude));
        d.Results.Add(new DetectionResult(
            new AnnotationKey("svc", "m", "*", "$.lng"),
            BuiltInSemanticTags.CoordinateLongitude));

        var prober = new FrameProber([d], autoLayer);
        prober.ObserveFrame(Ctx("svc", "m", "*", """{"lat": 1, "lng": 2}"""));

        Assert.Equal(BuiltInSemanticTags.CoordinateLatitude,
            autoLayer.Get(new AnnotationKey("svc", "m", "*", "$.lat")));
        Assert.Equal(BuiltInSemanticTags.CoordinateLongitude,
            autoLayer.Get(new AnnotationKey("svc", "m", "*", "$.lng")));
    }

    [Fact]
    public void Misbehaving_Detector_Does_Not_Prevent_Other_Detectors_From_Running()
    {
        var d1 = new ThrowingDetector();
        var d2 = new CountingDetector();
        var prober = new FrameProber([d1, d2], new InMemoryAnnotationLayer());

        // No exception escapes the prober — the throw is swallowed
        // and the second detector still runs.
        prober.ObserveFrame(Ctx("svc", "m", "*", """{"a": 1}"""));

        Assert.Equal(1, d2.Calls);
    }

    [Fact]
    public void Empty_Detector_Set_Still_Tracks_Probed_Triples()
    {
        var prober = new FrameProber([], new InMemoryAnnotationLayer());

        prober.ObserveFrame(Ctx("svc", "m", "*", """{"a": 1}"""));
        prober.ObserveFrame(Ctx("svc", "m", "*", """{"a": 2}"""));

        Assert.Equal(1, prober.ProbedTripleCount);
    }
}
