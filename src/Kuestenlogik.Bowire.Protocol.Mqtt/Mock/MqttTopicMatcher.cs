// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.Mqtt.Mock;

/// <summary>
/// MQTT topic-pattern matcher. Supports the two wildcards the MQTT
/// spec defines on subscriptions:
/// <list type="bullet">
///   <item><c>+</c> — exactly one level (e.g. <c>sensors/+/temp</c>
///   matches <c>sensors/room1/temp</c> but not <c>sensors/a/b/temp</c>).</item>
///   <item><c>#</c> — zero or more trailing levels, must be the last
///   segment (e.g. <c>sensors/#</c> matches <c>sensors</c>,
///   <c>sensors/a</c>, <c>sensors/a/b/c</c>).</item>
/// </list>
/// On a successful match, the positional wildcard values are returned
/// so callers can expose them to the dynamic-value substitutor as
/// <c>${topic.0}</c>, <c>${topic.1}</c>, …, and the <c>#</c> tail as
/// <c>${topic.rest}</c>.
/// </summary>
public static class MqttTopicMatcher
{
    /// <summary>
    /// Test whether <paramref name="topic"/> (a concrete MQTT topic)
    /// satisfies <paramref name="pattern"/> (a pattern that may carry
    /// <c>+</c> / <c>#</c> wildcards), and collect the wildcard
    /// bindings.
    /// </summary>
    /// <param name="pattern">Pattern from the recorded step, e.g. <c>cmd/+/set</c>.</param>
    /// <param name="topic">Concrete topic from the incoming publish.</param>
    /// <param name="bindings">
    /// When the match succeeds, populated with (positional key →
    /// captured segment). <c>+</c> wildcards contribute
    /// <c>"0"</c>/<c>"1"</c>/... by position; the trailing <c>#</c>
    /// wildcard also contributes <c>"rest"</c> with the full
    /// remainder of the topic. Empty when a literal pattern matches
    /// its literal topic.
    /// </param>
    /// <returns><c>true</c> on match, <c>false</c> otherwise.</returns>
    public static bool TryMatch(string? pattern, string topic, out Dictionary<string, string> bindings)
    {
        bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(pattern)) return false;
        if (string.IsNullOrEmpty(topic)) return pattern.Length == 0;

        var patternSegments = pattern.Split('/');
        var topicSegments = topic.Split('/');
        var wildcardIndex = 0;

        for (var i = 0; i < patternSegments.Length; i++)
        {
            var ps = patternSegments[i];

            if (ps == "#")
            {
                // Must be the last pattern segment. Captures every
                // remaining topic segment as a single joined string.
                if (i != patternSegments.Length - 1) return false;
                var rest = i < topicSegments.Length
                    ? string.Join("/", topicSegments, i, topicSegments.Length - i)
                    : string.Empty;
                bindings["rest"] = rest;
                bindings[wildcardIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)] = rest;
                return true;
            }

            if (i >= topicSegments.Length) return false;

            if (ps == "+")
            {
                bindings[wildcardIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)] = topicSegments[i];
                wildcardIndex++;
                continue;
            }

            if (!string.Equals(ps, topicSegments[i], StringComparison.Ordinal))
            {
                bindings.Clear();
                return false;
            }
        }

        // Only match if we consumed all topic segments — an unmatched
        // tail means the pattern was too short (no # catch-all) so the
        // topic shouldn't match.
        if (patternSegments.Length != topicSegments.Length)
        {
            bindings.Clear();
            return false;
        }
        return true;
    }

    /// <summary>True when the given pattern carries at least one wildcard (<c>+</c> or <c>#</c>).</summary>
    public static bool IsPattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        foreach (var segment in pattern.Split('/'))
        {
            if (segment == "+" || segment == "#") return true;
        }
        return false;
    }
}
