// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Recordings.Correlation;

namespace Kuestenlogik.Bowire.Recordings.Tests;

/// <summary>
/// #539 — the correlated timeline. The analyzer is the single
/// implementation behind both <c>POST /api/recordings/correlate</c> and
/// <c>bowire recording correlate</c>, so everything asserted here holds
/// for the workbench and the terminal alike.
/// </summary>
public sealed class RecordingCorrelationAnalyzerTests
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static BowireRecording LoadHarbor()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "port-call-1.bwr.json");
        var rec = JsonSerializer.Deserialize<BowireRecording>(File.ReadAllText(path), s_json);
        Assert.NotNull(rec);
        return rec;
    }

    private static BowireRecordingStep Step(
        string id,
        string protocol,
        long capturedAt,
        string? body = null,
        string? response = null,
        IDictionary<string, string>? metadata = null,
        long durationMs = 5)
    {
        return new BowireRecordingStep
        {
            Id = id,
            Protocol = protocol,
            Service = protocol + "-svc",
            Method = "Do",
            CapturedAt = capturedAt,
            DurationMs = durationMs,
            Body = body,
            Response = response,
            Metadata = metadata,
        };
    }

    // ---- (1) the flagship recording ----

    [Fact]
    public void Suggest_RanksTheSharedShipIdFirst_OnTheHarborRecording()
    {
        var suggestions = RecordingCorrelationAnalyzer.Suggest(LoadHarbor());

        Assert.NotEmpty(suggestions);
        var top = suggestions[0];
        // Spelling differs per protocol (ShipId / shipId); the analyzer
        // groups on the normalised form.
        Assert.Equal("shipid", new string(top.Name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()));
        Assert.Equal("101", top.Value);
        Assert.Equal(RecordingCorrelationKey.SourceField, top.Source);
        Assert.True(top.StepCount >= 3, $"expected the key on at least 3 steps, got {top.StepCount}");
    }

    [Fact]
    public void Suggest_DropsTheBareIdName_BecauseItCollidesAcrossEntities()
    {
        var suggestions = RecordingCorrelationAnalyzer.Suggest(LoadHarbor());

        // `id` appears on the gRPC ship, the GraphQL port call and every
        // container. Suggesting it would fuse three unrelated entities.
        Assert.DoesNotContain(suggestions, s => string.Equals(s.Name, "id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_TiesFiveProtocolsTogether_AndKeepsTheGrpcStepWeak()
    {
        var timeline = RecordingCorrelationAnalyzer.Analyze(LoadHarbor());

        Assert.NotNull(timeline.Key);
        Assert.Equal("101", timeline.Key.Value);
        Assert.Equal(8, timeline.Events.Count);
        Assert.Equal(8, timeline.Lanes.Count);

        // odata / rest / websocket / signalr / sse all carry a leaf whose
        // name ends in "shipid" — those are strong. The gRPC step only
        // carries `"id": 101`, which is the same value on a different
        // id-shaped leaf: honest, but weak.
        var strong = timeline.Events
            .Where(e => e.Match == RecordingCorrelationMatch.Strong)
            .Select(e => e.Protocol)
            .ToList();
        Assert.Equal(5, strong.Count);
        Assert.Contains("odata", strong);
        Assert.Contains("rest", strong);
        Assert.Contains("websocket", strong);
        Assert.Contains("signalr", strong);
        Assert.Contains("sse", strong);

        var grpc = timeline.Events.Single(e => e.Protocol == "grpc");
        Assert.Equal(RecordingCorrelationMatch.Weak, grpc.Match);

        // 7 of 8 since #545: five strong, one weak, and graphql joined
        // through a container id (see the derived-join tests below). mqtt
        // stays dark and is reported as such, not rounded up.
        //
        // Fixtures/port-call-1.bwr.json is an independent copy, not a mirror
        // of the shipped harbor sample: it is frozen at the pre-Bowire.Samples#54
        // shape, where the crane telemetry carried only `craneId`. The shipped
        // sample now also names the container being lifted and reaches 8 of 8.
        // Nothing enforces that the two stay otherwise identical — the sample
        // lives in Kuestenlogik/Bowire.Samples and may drift further; that is
        // expected and does not break this test. What this copy exists for is
        // the 7-of-8 outcome asserted below: the only realistic case in which
        // a whole lane is turned down for offering nothing but a coincidence,
        // which is the property the strictness is there to protect. Do not
        // "fix" it by re-syncing it with the sample.
        Assert.Equal(7, timeline.MatchedStepCount);
        Assert.Equal(7, timeline.MatchedProtocolCount);
        Assert.Equal(1, timeline.DerivedStepCount);
        Assert.Equal(1, timeline.DerivedProtocolCount);
    }

    [Fact]
    public void Analyze_PlacesEveryStepOnOneAxis_InFirstAppearanceLaneOrder()
    {
        var timeline = RecordingCorrelationAnalyzer.Analyze(LoadHarbor());

        Assert.Equal(
            ["grpc", "odata", "rest", "graphql", "websocket", "signalr", "sse", "mqtt"],
            timeline.Lanes.Select(l => l.Protocol).ToArray());

        Assert.Equal(0, timeline.Events[0].OffsetMs);
        Assert.Equal(150, timeline.Events[1].OffsetMs);
        Assert.Equal(1300, timeline.Events[7].OffsetMs);
        Assert.True(timeline.SpanMs >= 4300,
            $"the mqtt stream runs 3 s from +1300 ms, so the span must cover it; got {timeline.SpanMs}");
    }

    // ---- (2) headers outrank inferred fields ----

    [Fact]
    public void Suggest_PrefersACorrelationHeader_OverAnySharedField()
    {
        var trace = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        var rec = new BowireRecording { Id = "r", Name = "traced" };
        rec.Steps.Add(Step("s1", "rest", 0,
            response: """{"orderId":"7","total":12}""",
            metadata: new Dictionary<string, string> { ["traceparent"] = trace }));
        rec.Steps.Add(Step("s2", "grpc", 10,
            response: """{"orderId":"7","state":"PAID"}""",
            metadata: new Dictionary<string, string> { ["traceparent"] = trace }));

        var suggestions = RecordingCorrelationAnalyzer.Suggest(rec);
        var top = suggestions[0];

        Assert.Equal(RecordingCorrelationKey.SourceHeader, top.Source);
        // The trace-id, not the whole traceparent — the span-id changes
        // per hop and would never correlate.
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", top.Value);
        Assert.True(top.Score > RecordingCorrelationAnalyzer.HeaderScoreBase);

        var timeline = RecordingCorrelationAnalyzer.Analyze(rec);
        Assert.Equal(RecordingCorrelationKey.SourceHeader, timeline.Key!.Source);
        Assert.All(timeline.Events, e => Assert.Equal(RecordingCorrelationMatch.Strong, e.Match));
    }

    [Fact]
    public void Analyze_HeaderKeyHasNoWeakTier()
    {
        var trace = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01";
        var rec = new BowireRecording { Id = "r", Name = "traced" };
        rec.Steps.Add(Step("s1", "rest", 0, metadata: new Dictionary<string, string> { ["x-request-id"] = "abc" }));
        rec.Steps.Add(Step("s2", "grpc", 10, metadata: new Dictionary<string, string> { ["x-request-id"] = "abc" }));
        // Carries the value in the payload but not in the header.
        rec.Steps.Add(Step("s3", "mqtt", 20, response: """{"requestId":"abc"}""",
            metadata: new Dictionary<string, string> { ["traceparent"] = trace }));

        var key = new RecordingCorrelationKey("x-request-id", "abc", RecordingCorrelationKey.SourceHeader);
        var timeline = RecordingCorrelationAnalyzer.Analyze(rec, key);

        Assert.Equal(RecordingCorrelationMatch.Strong, timeline.Events[0].Match);
        Assert.Equal(RecordingCorrelationMatch.Strong, timeline.Events[1].Match);
        Assert.Equal(RecordingCorrelationMatch.None, timeline.Events[2].Match);
    }

    // ---- (3) no signal at all ----

    [Fact]
    public void Analyze_WithNoSharedSignal_StillProducesLanesAndAnAxis()
    {
        var rec = new BowireRecording { Id = "r", Name = "unrelated" };
        rec.Steps.Add(Step("s1", "rest", 100, response: """{"alpha":1}""", durationMs: 12));
        rec.Steps.Add(Step("s2", "grpc", 400, response: """{"beta":2}""", durationMs: 30));

        var timeline = RecordingCorrelationAnalyzer.Analyze(rec);

        Assert.Null(timeline.Key);
        Assert.Empty(timeline.Suggestions);
        Assert.All(timeline.Events, e => Assert.Equal(RecordingCorrelationMatch.None, e.Match));
        Assert.Equal(0, timeline.MatchedStepCount);
        Assert.Equal(2, timeline.Lanes.Count);
        Assert.Equal(0, timeline.Events[0].OffsetMs);
        Assert.Equal(300, timeline.Events[1].OffsetMs);
        Assert.True(timeline.SpanMs >= 330);
    }

    [Fact]
    public void Analyze_ResolvedKeyThatMatchesNothing_Warns()
    {
        var rec = new BowireRecording { Id = "r", Name = "unrelated" };
        rec.Steps.Add(Step("s1", "rest", 0, response: """{"alpha":1}"""));

        var timeline = RecordingCorrelationAnalyzer.Analyze(
            rec, new RecordingCorrelationKey("shipId", "999", RecordingCorrelationKey.SourceField));

        Assert.Equal(0, timeline.MatchedStepCount);
        Assert.Contains(timeline.Warnings, w => w.Contains("matched no step", StringComparison.Ordinal));
    }

    // ---- (4) timebase ----

    [Fact]
    public void Analyze_AuthoredRelativeStamps_ReportRelativeTimebase()
    {
        var timeline = RecordingCorrelationAnalyzer.Analyze(LoadHarbor());

        Assert.Equal(RecordingCorrelationTimeline.TimebaseRelative, timeline.Timebase);
        Assert.Equal(0, timeline.OriginMs);
    }

    [Fact]
    public void Analyze_LiveEpochStamps_ReportAbsoluteTimebase()
    {
        const long now = 1_784_678_400_000L;
        var rec = new BowireRecording { Id = "r", Name = "live" };
        rec.Steps.Add(Step("s1", "rest", now));
        rec.Steps.Add(Step("s2", "rest", now + 250));

        var timeline = RecordingCorrelationAnalyzer.Analyze(rec);

        Assert.Equal(RecordingCorrelationTimeline.TimebaseAbsolute, timeline.Timebase);
        Assert.Equal(now, timeline.OriginMs);
        Assert.Equal(0, timeline.Events[0].OffsetMs);
        Assert.Equal(250, timeline.Events[1].OffsetMs);
    }

    [Fact]
    public void Analyze_AllZeroStamps_FallsBackToCumulativeDurations_AndSaysSo()
    {
        var rec = new BowireRecording { Id = "r", Name = "unstamped" };
        rec.Steps.Add(Step("s1", "rest", 0, durationMs: 40));
        rec.Steps.Add(Step("s2", "grpc", 0, durationMs: 60));
        rec.Steps.Add(Step("s3", "mqtt", 0, durationMs: 10));

        var timeline = RecordingCorrelationAnalyzer.Analyze(rec);

        Assert.Equal(0, timeline.Events[0].OffsetMs);
        Assert.Equal(40, timeline.Events[1].OffsetMs);
        Assert.Equal(100, timeline.Events[2].OffsetMs);
        Assert.Contains(timeline.Warnings, w => w.Contains("capturedAt = 0", StringComparison.Ordinal));
    }

    // ---- (5) streaming frames ----

    [Fact]
    public void Analyze_FrameOffsets_AreStepOffsetPlusFrameTimestamp()
    {
        var timeline = RecordingCorrelationAnalyzer.Analyze(LoadHarbor());

        // signalr starts at +900 ms and its frames are stamped 200 / 1200 / …
        var signalr = timeline.Events.Single(e => e.Protocol == "signalr");
        Assert.Equal(900, signalr.OffsetMs);
        Assert.NotEmpty(signalr.Frames);
        Assert.Equal(1100, signalr.Frames[0].OffsetMs);
        Assert.Equal(2100, signalr.Frames[1].OffsetMs);
        Assert.All(signalr.Frames, f => Assert.Equal(RecordingCorrelationMatch.Strong, f.Match));

        // mqtt frames carry craneId, not shipId — no match, but they are
        // still placed on the axis.
        var mqtt = timeline.Events.Single(e => e.Protocol == "mqtt");
        Assert.NotEmpty(mqtt.Frames);
        Assert.All(mqtt.Frames, f => Assert.Equal(RecordingCorrelationMatch.None, f.Match));
        Assert.Equal(1300, mqtt.Frames[0].OffsetMs);
    }

    // ---- (6) key precedence ----

    [Fact]
    public void Analyze_PersistedCorrelationField_OverridesTheAutoSuggestion()
    {
        var rec = LoadHarbor();
        rec.Correlation = new BowireRecordingCorrelation("craneId", "1");

        var timeline = RecordingCorrelationAnalyzer.Analyze(rec);

        Assert.Equal("craneId", timeline.Key!.Name);
        Assert.Equal("1", timeline.Key.Value);
        Assert.Equal(RecordingCorrelationKey.SourceField, timeline.Key.Source);
        Assert.Equal(RecordingCorrelationMatch.Strong,
            timeline.Events.Single(e => e.Protocol == "mqtt").Match);
        // shipId would still have been the auto-pick — the suggestion
        // list is unaffected by the override.
        Assert.Contains(timeline.Suggestions, s => s.Value == "101");
    }

    [Fact]
    public void Analyze_ExplicitArgument_OverridesThePersistedField()
    {
        var rec = LoadHarbor();
        rec.Correlation = new BowireRecordingCorrelation("craneId", "1");

        var timeline = RecordingCorrelationAnalyzer.Analyze(
            rec, new RecordingCorrelationKey("shipId", "101", RecordingCorrelationKey.SourceField));

        Assert.Equal("shipId", timeline.Key!.Name);
        Assert.Equal(7, timeline.MatchedStepCount);
    }

    [Fact]
    public void ResolveSource_RecognisesKnownCorrelationHeaders_AndNothingElse()
    {
        Assert.Equal(RecordingCorrelationKey.SourceHeader, RecordingCorrelationAnalyzer.ResolveSource("traceparent"));
        Assert.Equal(RecordingCorrelationKey.SourceHeader, RecordingCorrelationAnalyzer.ResolveSource("X-Correlation-Id"));
        Assert.Equal(RecordingCorrelationKey.SourceField, RecordingCorrelationAnalyzer.ResolveSource("shipId"));
        Assert.Equal(RecordingCorrelationKey.SourceNone, RecordingCorrelationAnalyzer.ResolveSource(null));
    }

    // ---- (7) the second edge — joining a renamed identifier (#545) ----
    //
    // A transaction that renames its identifier as it crosses services
    // lights only the lanes that speak the seed key. Every test in this
    // block is about which shared values are allowed to close that gap,
    // and — at least as importantly — which are not.

    [Fact]
    public void Analyze_JoinsTheGraphqlLane_ThroughAContainerIdItSharesWithRest()
    {
        var timeline = RecordingCorrelationAnalyzer.Analyze(LoadHarbor());

        // GraphQL calls the same transaction portCall.id = 1 and carries
        // shipId nowhere, so the seed key cannot reach it. It shares three
        // container ids with the REST step, and those are distinctive.
        var graphql = timeline.Events.Single(e => e.Protocol == "graphql");
        Assert.Equal(RecordingCorrelationMatch.Derived, graphql.Match);
        Assert.NotNull(graphql.Link);
        Assert.Equal("MSCU1234567", graphql.Link.Value);
        Assert.Equal("id", graphql.Link.Name);
        Assert.Equal("id", graphql.Link.ViaName);
        Assert.Equal("rest", graphql.Link.ViaProtocol);
        Assert.Equal("step_03_gate_containers", graphql.Link.ViaStepId);
        Assert.Equal(2, graphql.Link.ViaStepIndex);
        // Three containers tie exactly. Naming one without admitting the
        // other two would read as though that container were special.
        Assert.Equal(2, graphql.Link.AlternativeCount);

        var lane = timeline.Lanes.Single(l => l.Protocol == "graphql");
        Assert.Equal(1, lane.MatchedCount);
        Assert.Equal(1, lane.DerivedCount);

        // Only a derived step carries a link — a strong or weak verdict
        // stands on the key itself and has nothing to explain.
        Assert.All(
            timeline.Events.Where(e => e.Match != RecordingCorrelationMatch.Derived),
            e => Assert.Null(e.Link));
    }

    [Fact]
    public void Analyze_LeavesTheMqttLaneDark_BecauseTheOnlyValueItSharesIsTheNumberOne()
    {
        var timeline = RecordingCorrelationAnalyzer.Analyze(LoadHarbor());

        // The crane telemetry shares exactly one value with the rest of
        // the capture: the integer 1, on craneId. That same 1 is also a
        // dock number, a sequence number and the port-call id. Joining on
        // it would fuse four unrelated entities, so the eighth lane stays
        // dark — and says so rather than going quietly missing.
        var mqtt = timeline.Events.Single(e => e.Protocol == "mqtt");
        Assert.Equal(RecordingCorrelationMatch.None, mqtt.Match);
        Assert.Null(mqtt.Link);
        Assert.DoesNotContain(timeline.Events, e => e.Link is not null && e.Link.Value == "1");
        Assert.Contains(timeline.Warnings, w => w.Contains("craneId = 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_ARenamedIdentifier_IsReachedOnlyByTheSecondEdge()
    {
        // Shaped like harbor: two services agree on shipId, a third calls
        // the same transaction by its own id and shares only the manifest.
        var rec = new BowireRecording { Id = "r", Name = "renamed" };
        rec.Steps.Add(Step("s1", "rest", 0,
            response: """{"shipId":"9001","containers":[{"id":"MSCU1234567"},{"id":"HLBU2345678"}]}"""));
        rec.Steps.Add(Step("s2", "odata", 10, response: """{"ShipId":"9001"}"""));
        rec.Steps.Add(Step("s3", "graphql", 20,
            response: """{"data":{"portCall":{"id":"77","containers":[{"id":"MSCU1234567"}]}}}"""));

        var timeline = RecordingCorrelationAnalyzer.Analyze(rec);

        Assert.Equal("9001", timeline.Key!.Value);
        // The seed key alone cannot reach s3 — 9001 is not in its payload
        // at all. Without the second edge this is a 2/3 recording.
        Assert.DoesNotContain("9001", rec.Steps[2].Response, StringComparison.Ordinal);

        var bff = timeline.Events[2];
        Assert.Equal(RecordingCorrelationMatch.Derived, bff.Match);
        Assert.Equal("MSCU1234567", bff.Link!.Value);
        Assert.Equal("rest", bff.Link.ViaProtocol);
        // HLBU2345678 never reaches s3, so there is no runner-up here.
        Assert.Equal(0, bff.Link.AlternativeCount);
        Assert.Equal(3, timeline.MatchedStepCount);
        Assert.Equal(1, timeline.DerivedStepCount);
    }

    [Fact]
    public void Analyze_AWeaklyMatchedStep_IsNeitherBridgedNorReportedAsRejected()
    {
        // s3 is already on the timeline as `weak`: it carries the seed value
        // on a generically named leaf. It ALSO carries a container id that
        // would qualify as a bridge. Joining it would overwrite an honest
        // `weak` verdict with `derived` and inflate DerivedStepCount, and
        // the same step would be named in the rejected-bridge warning while
        // its lane is visibly lit.
        var rec = new BowireRecording { Id = "r", Name = "weak-bridge" };
        rec.Steps.Add(Step("s1", "rest", 0,
            response: """{"shipId":"9001","containers":[{"id":"MSCU1234567"}]}"""));
        rec.Steps.Add(Step("s2", "odata", 10, response: """{"ShipId":"9001"}"""));
        rec.Steps.Add(Step("s3", "grpc", 20,
            response: """{"id":"9001","containerId":"MSCU1234567"}"""));

        var timeline = RecordingCorrelationAnalyzer.Analyze(rec);

        Assert.Equal("9001", timeline.Key!.Value);
        var grpc = timeline.Events.Single(e => e.Protocol == "grpc");
        Assert.Equal(RecordingCorrelationMatch.Weak, grpc.Match);
        Assert.Null(grpc.Link);
        Assert.Equal(0, timeline.DerivedStepCount);
        Assert.DoesNotContain(timeline.Warnings, w => w.Contains("grpc (", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_TheSameIdUnderTwoSpellings_IsNotPenalisedAgainstAnIdCarriedByFewerSteps()
    {
        // Both containers are 11 characters and both reach the dark step,
        // so the tiebreak decides. MSCU is corroborated by two lit steps
        // and answers to `id` on one and `containerId` on the other —
        // the same identifier under two spellings, which is exactly the
        // shape gate 3 admits. Counting that as a spread of 2 would demote
        // the best-corroborated value in favour of the least.
        // Six steps so that a value on three of them clears gate 4's
        // selectivity ceiling — this test is about the tiebreak, not that.
        var rec = new BowireRecording { Id = "r", Name = "spread" };
        rec.Steps.Add(Step("s1", "rest", 0,
            response: """{"shipId":"9001","containers":[{"id":"MSCU1234567"},{"id":"HLBU2345678"}]}"""));
        rec.Steps.Add(Step("s2", "odata", 10,
            response: """{"ShipId":"9001","containerId":"MSCU1234567"}"""));
        rec.Steps.Add(Step("s3", "websocket", 20, response: """{"shipId":"9001"}"""));
        rec.Steps.Add(Step("s4", "signalr", 30, response: """{"ShipId":"9001"}"""));
        rec.Steps.Add(Step("s5", "sse", 40, response: """{"shipId":"9001"}"""));
        rec.Steps.Add(Step("s6", "graphql", 50,
            response: """{"data":{"portCall":{"id":"77","containers":[{"id":"MSCU1234567"},{"id":"HLBU2345678"}]}}}"""));

        var timeline = RecordingCorrelationAnalyzer.Analyze(rec);

        var graphql = timeline.Events.Single(e => e.Protocol == "graphql");
        Assert.Equal(RecordingCorrelationMatch.Derived, graphql.Match);
        Assert.Equal("MSCU1234567", graphql.Link!.Value);
        Assert.Equal(1, graphql.Link.AlternativeCount);
    }

    [Fact]
    public void Analyze_ABareIntegerNeverBridges_EvenWhenTwoStepsAgreeOnIt()
    {
        var rec = new BowireRecording { Id = "r", Name = "collide" };
        rec.Steps.Add(Step("s1", "rest", 0, response: """{"orderId":"AC-77120-QX","seatId":1}"""));
        rec.Steps.Add(Step("s2", "grpc", 10, response: """{"orderId":"AC-77120-QX"}"""));
        // A different entity that happens to be number 1.
        rec.Steps.Add(Step("s3", "mqtt", 20, response: """{"craneId":1}"""));

        var timeline = RecordingCorrelationAnalyzer.Analyze(rec);

        Assert.Equal("AC-77120-QX", timeline.Key!.Value);
        Assert.Equal(RecordingCorrelationMatch.None, timeline.Events[2].Match);
        Assert.Null(timeline.Events[2].Link);
        Assert.Equal(0, timeline.DerivedStepCount);
        Assert.Contains(timeline.Warnings, w => w.Contains("craneId = 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_ABooleanOnAnIdShapedLeafNeverBridges()
    {
        var rec = new BowireRecording { Id = "r", Name = "boolean" };
        rec.Steps.Add(Step("s1", "rest", 0, response: """{"orderId":"AC-77120-QX","verifiedId":true}"""));
        rec.Steps.Add(Step("s2", "grpc", 10, response: """{"orderId":"AC-77120-QX"}"""));
        rec.Steps.Add(Step("s3", "mqtt", 20, response: """{"verifiedId":true}"""));

        var timeline = RecordingCorrelationAnalyzer.Analyze(rec);

        // `true` is id-shaped by name and its names cohere perfectly — the
        // distinctiveness floor is the only thing standing between two
        // steps and a join on a two-valued field.
        Assert.Equal(RecordingCorrelationMatch.None, timeline.Events[2].Match);
        Assert.Equal(0, timeline.DerivedStepCount);
    }

    [Fact]
    public void Analyze_ARepeatedStatusStringNeverBridges_BecauseItsFieldNamesDoNotCohere()
    {
        var rec = new BowireRecording { Id = "r", Name = "enum" };
        rec.Steps.Add(Step("s1", "rest", 0,
            response: """{"orderId":"AC-77120-QX","statusId":"Loading","status":"Loading"}"""));
        rec.Steps.Add(Step("s2", "grpc", 10, response: """{"orderId":"AC-77120-QX"}"""));
        rec.Steps.Add(Step("s3", "mqtt", 20, response: """{"statusId":"Loading"}"""));

        var timeline = RecordingCorrelationAnalyzer.Analyze(rec);

        // "Loading" is 7 characters, so the length floor lets it through.
        // What stops it is that the same value is also carried by a leaf
        // called `status`, which is not id-shaped: a value doing two jobs
        // is a label, not an identifier.
        Assert.Equal(RecordingCorrelationMatch.None, timeline.Events[2].Match);
        Assert.Null(timeline.Events[2].Link);
        Assert.Contains(timeline.Warnings, w => w.Contains("statusId = Loading", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_DepthIsTwoEdges_SoTheChainStopsAfterTheFirstBridge()
    {
        var rec = new BowireRecording { Id = "r", Name = "chain" };
        rec.Steps.Add(Step("s1", "rest", 0,
            response: """{"orderId":"AC-77120-QX","bookingId":"BK-4471209"}"""));
        rec.Steps.Add(Step("s2", "grpc", 10,
            response: """{"bookingId":"BK-4471209","parcelId":"PZ-9930881"}"""));
        rec.Steps.Add(Step("s3", "mqtt", 20, response: """{"parcelId":"PZ-9930881"}"""));

        var timeline = RecordingCorrelationAnalyzer.Analyze(
            rec, new RecordingCorrelationKey("orderId", "AC-77120-QX", RecordingCorrelationKey.SourceField));

        Assert.Equal(RecordingCorrelationMatch.Strong, timeline.Events[0].Match);
        Assert.Equal(RecordingCorrelationMatch.Derived, timeline.Events[1].Match);
        Assert.Equal("BK-4471209", timeline.Events[1].Link!.Value);

        // s3 shares a perfectly distinctive value with s2 — but s2 is
        // itself only on the transaction by inference, and following it
        // would be a third edge. This is the whole depth cap in one
        // assertion: relax it and an id-rich recording relates everything
        // to everything.
        Assert.Equal(RecordingCorrelationMatch.None, timeline.Events[2].Match);
        Assert.Null(timeline.Events[2].Link);
        Assert.Equal(2, timeline.MatchedStepCount);
        Assert.Equal(1, timeline.DerivedStepCount);
    }

    [Fact]
    public void Analyze_ADerivedStreamingStep_LightsOnlyTheFramesCarryingTheBridge()
    {
        var rec = new BowireRecording { Id = "r", Name = "stream" };
        rec.Steps.Add(Step("s1", "rest", 0,
            response: """{"orderId":"AC-77120-QX","parcelId":"PZ-9930881"}"""));
        rec.Steps.Add(new BowireRecordingStep
        {
            Id = "s2",
            Protocol = "mqtt",
            Service = "telemetry",
            Method = "receive",
            MethodType = "ServerStreaming",
            CapturedAt = 100,
            DurationMs = 300,
            ReceivedMessages =
            [
                new BowireRecordingFrame { Index = 0, TimestampMs = 0, Body = """{"parcelId":"PZ-9930881"}""" },
                new BowireRecordingFrame { Index = 1, TimestampMs = 100, Body = """{"craneId":1}""" },
            ],
        });

        var timeline = RecordingCorrelationAnalyzer.Analyze(
            rec, new RecordingCorrelationKey("orderId", "AC-77120-QX", RecordingCorrelationKey.SourceField));

        var stream = timeline.Events[1];
        Assert.Equal(RecordingCorrelationMatch.Derived, stream.Match);
        // Without rebuilding the frames against the bridge value, a lit
        // bar would sit over two dead ticks.
        Assert.Equal(RecordingCorrelationMatch.Derived, stream.Frames[0].Match);
        Assert.Equal(RecordingCorrelationMatch.None, stream.Frames[1].Match);
    }

    [Fact]
    public void Analyze_TheJoinNeverFeedsBackIntoTheSuggestionList()
    {
        var timeline = RecordingCorrelationAnalyzer.Analyze(LoadHarbor());

        // A bridge is not a candidate key. If derived edges leaked into
        // candidate scoring, the picker would start offering one arbitrary
        // container id and the suggestion order would drift as the join
        // grew.
        Assert.DoesNotContain(timeline.Suggestions, s => s.Value == "MSCU1234567");
        Assert.Equal(
            RecordingCorrelationAnalyzer.Suggest(LoadHarbor()).Select(s => s.Name + "=" + s.Value).ToArray(),
            timeline.Suggestions.Select(s => s.Name + "=" + s.Value).ToArray());
    }

    // ---- guards ----

    [Fact]
    public void Analyze_EmptyRecording_IsNotAnError()
    {
        var timeline = RecordingCorrelationAnalyzer.Analyze(new BowireRecording { Id = "e", Name = "empty" });

        Assert.Empty(timeline.Events);
        Assert.Empty(timeline.Lanes);
        Assert.Equal(1, timeline.SpanMs);
        Assert.Null(timeline.Key);
    }

    [Fact]
    public void Analyze_MalformedPayloads_AreSkippedRatherThanThrowing()
    {
        var rec = new BowireRecording { Id = "r", Name = "mixed" };
        // Raw GraphQL SDL, a bare word and a truncated document — none of
        // these are JSON and none of them may take the analysis down.
        rec.Steps.Add(Step("s1", "graphql", 0, body: "type Query { ship(id: ID!): Ship }"));
        rec.Steps.Add(Step("s2", "rest", 5, body: "not-json", response: """{"shipId":101}"""));
        rec.Steps.Add(Step("s3", "rest", 9, body: """{"shipId":101,"""));

        var timeline = RecordingCorrelationAnalyzer.Analyze(rec);

        Assert.Equal(3, timeline.Events.Count);
        Assert.Equal(RecordingCorrelationMatch.None, timeline.Events[0].Match);
    }

    // ---- (7) interpretation payloads are a surface too (#547) ----

    private static RecordedInterpretation Interpretation(string json)
        => new("geo.point", "$.payload", JsonDocument.Parse(json).RootElement.Clone());

    /// <summary>
    /// A step whose only identifier lives in an interpretation payload.
    /// Built inline rather than through <see cref="Step"/> because
    /// <c>Interpretations</c> is init-only.
    /// </summary>
    private static BowireRecordingStep StepWithInterpretation(
        string id, string protocol, long capturedAt, string body, string payloadJson)
        => new()
        {
            Id = id,
            Protocol = protocol,
            Service = protocol + "-svc",
            Method = "Do",
            CapturedAt = capturedAt,
            DurationMs = 5,
            Body = body,
            Interpretations = [Interpretation(payloadJson)],
        };

    [Fact]
    public void Suggest_ReadsAnIdentifierThatOnlyExistsInAnInterpretationPayload()
    {
        // The scanner called itself "every JSON-bearing surface of one step"
        // and skipped this one. An interpretation payload is where a semantic
        // widget's data lives, and it survives save/load verbatim — so a
        // recording can legitimately carry its only shared identifier there.
        var rec = new BowireRecording { Id = "r", Name = "interpretations" };
        rec.Steps.Add(StepWithInterpretation(
            "s1", "rest", 0,
            body: """{"note":"no ids here"}""",
            payloadJson: """{"vesselRef":"IMO-9074729","lat":53.5}"""));
        rec.Steps.Add(StepWithInterpretation(
            "s2", "grpc", 10,
            body: """{"note":"nor here"}""",
            payloadJson: """{"vesselRef":"IMO-9074729","lon":9.9}"""));

        var suggestions = RecordingCorrelationAnalyzer.Suggest(rec);

        var hit = Assert.Single(
            suggestions,
            c => string.Equals(c.Value, "IMO-9074729", StringComparison.Ordinal));
        Assert.Equal(2, hit.StepCount);
    }

    [Fact]
    public void Analyze_CorrelatesTwoStepsThatShareOnlyAnInterpretationPayload()
    {
        // The observable consequence of the gap: both steps stayed dark on
        // the correlated timeline, and nothing on screen said why.
        var rec = new BowireRecording { Id = "r", Name = "interpretations" };
        rec.Steps.Add(StepWithInterpretation(
            "s1", "rest", 0,
            body: """{"note":"no ids here"}""",
            payloadJson: """{"vesselRef":"IMO-9074729"}"""));
        rec.Steps.Add(StepWithInterpretation(
            "s2", "grpc", 10,
            body: """{"note":"nor here"}""",
            payloadJson: """{"vesselRef":"IMO-9074729"}"""));

        var timeline = RecordingCorrelationAnalyzer.Analyze(rec);

        Assert.NotNull(timeline.Key);
        Assert.Equal("IMO-9074729", timeline.Key.Value);
        Assert.Equal(2, timeline.Events.Count);
        Assert.All(timeline.Events, e => Assert.NotEqual(RecordingCorrelationMatch.None, e.Match));
    }

    [Fact]
    public void ScanFrame_ReadsAFramesInterpretationPayload()
    {
        // ScanFrame walked Body and Data but not Interpretations, so a
        // streaming frame had the same hole as its step.
        var frame = new BowireRecordingFrame
        {
            Body = """{"note":"no ids here"}""",
            Interpretations = [Interpretation("""{"vesselRef":"IMO-9074729"}""")],
        };

        var seen = new List<string>();
        RecordingCorrelationScanner.ScanFrame(frame, (_, _, value) => seen.Add(value));

        Assert.Contains("IMO-9074729", seen);
    }

    [Fact]
    public void AnInterpretationWithoutAPayloadIsSkippedRatherThanThrowing()
    {
        // A default JsonElement has kind Undefined, which is what an
        // interpretation written without a payload deserialises to.
        var frame = new BowireRecordingFrame
        {
            Body = """{"shipId":101}""",
            Interpretations = [new RecordedInterpretation("geo.point", "$.payload", default)],
        };

        var seen = new List<string>();
        RecordingCorrelationScanner.ScanFrame(frame, (_, _, value) => seen.Add(value));

        Assert.Equal(["101"], seen);
    }
}
