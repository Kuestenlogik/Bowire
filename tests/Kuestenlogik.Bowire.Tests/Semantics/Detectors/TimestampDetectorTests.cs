// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Semantics;
using Kuestenlogik.Bowire.Semantics.Detectors;

namespace Kuestenlogik.Bowire.Tests.Semantics.Detectors;

public sealed class TimestampDetectorTests
{
    // Frozen clock at 2026-05-11 — keeps the ±20-year plausibility
    // window deterministic so epoch tests don't flake near year boundaries.
    private static readonly DateTimeOffset s_pinnedNow =
        new(2026, 5, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimestampDetector s_detector =
        new(new PinnedTimeProvider(s_pinnedNow));

    private static DetectionContext Ctx(string json)
        => new("svc", "m", AnnotationKey.Wildcard, JsonDocument.Parse(json).RootElement);

    private sealed class PinnedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void Matches_Iso8601_String_Named_Timestamp()
    {
        var ctx = Ctx("""{"timestamp": "2026-05-11T12:00:00Z"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
        Assert.Equal("$.timestamp", results[0].Key.JsonPath);
        Assert.Equal(BuiltInSemanticTags.TimeseriesTimestamp, results[0].Semantic);
    }

    [Fact]
    public void Matches_CreatedAt()
    {
        var ctx = Ctx("""{"createdAt": "2026-05-11T12:00:00Z"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
    }

    [Fact]
    public void Matches_EventTime_Field()
    {
        var ctx = Ctx("""{"eventTime": "2026-05-11T12:00:00Z"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
    }

    [Fact]
    public void Matches_Epoch_Seconds_In_Plausible_Range()
    {
        // 1715472000 = 2024-05-12 around the pinned-now — well within ±20 years.
        var ctx = Ctx("""{"timestamp": 1715472000}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
    }

    [Fact]
    public void Matches_Epoch_Milliseconds_In_Plausible_Range()
    {
        // 1715472000000 = same instant in milliseconds.
        var ctx = Ctx("""{"updatedAt": 1715472000000}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
    }

    [Fact]
    public void Match_Is_Case_Insensitive()
    {
        var ctx = Ctx("""{"Timestamp": "2026-05-11T12:00:00Z"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
    }

    [Fact]
    public void Rejects_Field_Without_Matching_Name()
    {
        // "42" looks like a tiny epoch second but the field name doesn't
        // hint at time. No fire.
        var ctx = Ctx("""{"counter": 42}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Rejects_String_That_Is_Not_Iso8601()
    {
        var ctx = Ctx("""{"timestamp": "not-a-real-timestamp"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Rejects_Implausible_Epoch_Tiny_Number()
    {
        // 42 seconds since 1970 — would be ~1970-01-01 00:00:42.
        // Outside the ±20-years-of-now window.
        var ctx = Ctx("""{"timestamp": 42}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Rejects_Implausible_Epoch_Huge_Number()
    {
        // Around year 5000+.
        var ctx = Ctx("""{"timestamp": 99999999999999}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Rejects_Name_With_At_Substring_But_Not_End()
    {
        // "data", "atmosphere" must not match — "at" only matches as
        // a name suffix.
        var ctx = Ctx("""{"data": "2026-05-11T12:00:00Z", "atmosphere": "2026-05-11T12:00:00Z"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Walks_Into_Nested_Fields()
    {
        var ctx = Ctx("""{"event": {"timestamp": "2026-05-11T12:00:00Z"}}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
        Assert.Equal("$.event.timestamp", results[0].Key.JsonPath);
    }

    [Fact]
    public void Matches_Multiple_Timestamps_In_One_Frame()
    {
        var ctx = Ctx("""{"createdAt": "2026-05-11T12:00:00Z", "updatedAt": "2026-05-11T13:00:00Z"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Carries_Discriminator_Through_Into_Keys()
    {
        var raw = JsonDocument.Parse("""{"timestamp": "2026-05-11T12:00:00Z"}""");
        var ctx = new DetectionContext("svc", "m", "PositionUpdate", raw.RootElement);

        var results = s_detector.Detect(in ctx).ToList();

        Assert.All(results, r => Assert.Equal("PositionUpdate", r.Key.MessageType));
    }
}
