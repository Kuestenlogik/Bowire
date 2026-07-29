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

        // 6 of 8: graphql's id lives inside a query STRING and mqtt only
        // knows about craneId. Reported as it is, not rounded up.
        Assert.Equal(6, timeline.MatchedStepCount);
        Assert.Equal(6, timeline.MatchedProtocolCount);
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
        Assert.Equal(6, timeline.MatchedStepCount);
    }

    [Fact]
    public void ResolveSource_RecognisesKnownCorrelationHeaders_AndNothingElse()
    {
        Assert.Equal(RecordingCorrelationKey.SourceHeader, RecordingCorrelationAnalyzer.ResolveSource("traceparent"));
        Assert.Equal(RecordingCorrelationKey.SourceHeader, RecordingCorrelationAnalyzer.ResolveSource("X-Correlation-Id"));
        Assert.Equal(RecordingCorrelationKey.SourceField, RecordingCorrelationAnalyzer.ResolveSource("shipId"));
        Assert.Equal(RecordingCorrelationKey.SourceNone, RecordingCorrelationAnalyzer.ResolveSource(null));
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
}
