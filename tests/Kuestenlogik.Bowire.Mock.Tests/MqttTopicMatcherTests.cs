// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mock.Matchers;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Pins the MQTT topic-pattern matcher: single-level wildcard (+),
/// multi-level wildcard (#), literal segments, binding capture with
/// positional + "rest" keys, and the corner cases where the pattern
/// and topic align.
/// </summary>
public sealed class MqttTopicMatcherTests
{
    [Theory]
    [InlineData("sensors/temperature", "sensors/temperature", true)]
    [InlineData("sensors/temperature", "sensors/humidity", false)]
    [InlineData("sensors/temperature", "sensors/temperature/extra", false)]
    public void LiteralPattern_ExactMatchOnly(string pattern, string topic, bool expected)
    {
        var matched = MqttTopicMatcher.TryMatch(pattern, topic, out _);
        Assert.Equal(expected, matched);
    }

    [Fact]
    public void PlusWildcard_CapturesOneSegment()
    {
        var matched = MqttTopicMatcher.TryMatch("sensors/+/temp", "sensors/room1/temp", out var bindings);
        Assert.True(matched);
        Assert.Equal("room1", bindings["0"]);
    }

    [Fact]
    public void PlusWildcard_DoesNotMatchAcrossSegments()
    {
        var matched = MqttTopicMatcher.TryMatch("sensors/+/temp", "sensors/a/b/temp", out _);
        Assert.False(matched);
    }

    [Fact]
    public void MultiplePlusWildcards_CapturedByPosition()
    {
        var matched = MqttTopicMatcher.TryMatch(
            "cmd/+/device/+/exec", "cmd/floor1/device/d42/exec", out var bindings);
        Assert.True(matched);
        Assert.Equal("floor1", bindings["0"]);
        Assert.Equal("d42", bindings["1"]);
    }

    [Fact]
    public void HashWildcard_CapturesTrailingSegmentsAsRest()
    {
        var matched = MqttTopicMatcher.TryMatch("sensors/#", "sensors/room1/temp", out var bindings);
        Assert.True(matched);
        Assert.Equal("room1/temp", bindings["rest"]);
        Assert.Equal("room1/temp", bindings["0"]);
    }

    [Fact]
    public void HashWildcard_MatchesEvenWithNoTrailingSegments()
    {
        var matched = MqttTopicMatcher.TryMatch("sensors/#", "sensors", out var bindings);
        Assert.True(matched);
        Assert.Equal(string.Empty, bindings["rest"]);
    }

    [Fact]
    public void HashWildcard_MustBeLastSegment()
    {
        // "#/more" isn't a legal MQTT topic filter; if a pattern
        // happens to look like that the matcher refuses to match.
        var matched = MqttTopicMatcher.TryMatch("sensors/#/extra", "sensors/a/extra", out _);
        Assert.False(matched);
    }

    [Fact]
    public void MixedWildcards_PlusAndTrailingHash()
    {
        var matched = MqttTopicMatcher.TryMatch("cmd/+/all/#", "cmd/floor2/all/device/status/now", out var bindings);
        Assert.True(matched);
        Assert.Equal("floor2", bindings["0"]);
        Assert.Equal("device/status/now", bindings["rest"]);
    }

    [Fact]
    public void EmptyOrNullInputs_AreHandledGracefully()
    {
        Assert.False(MqttTopicMatcher.TryMatch(null, "sensors/x", out _));
        Assert.False(MqttTopicMatcher.TryMatch("sensors/x", "", out _));
        Assert.False(MqttTopicMatcher.TryMatch("", "", out _));
    }

    [Theory]
    [InlineData("sensors/+/temp", true)]
    [InlineData("sensors/#", true)]
    [InlineData("sensors/temperature", false)]
    [InlineData("", false)]
    public void IsPattern_DetectsWildcards(string pattern, bool expected)
    {
        Assert.Equal(expected, MqttTopicMatcher.IsPattern(pattern));
    }
}
