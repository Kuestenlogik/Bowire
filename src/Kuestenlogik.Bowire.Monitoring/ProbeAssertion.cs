// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Kuestenlogik.Bowire.Monitoring;

/// <summary>
/// A must-pass predicate over a probe's response. v1 covers the three checks a
/// black-box health probe actually needs: an expected status, a latency budget,
/// and a body substring. Richer predicates (JSON-path, header, regex) converge
/// on the shared Flows expectation DSL in a follow-up; this minimal set keeps
/// the Core engine self-contained.
/// </summary>
public sealed class ProbeAssertion
{
    /// <summary>What to check.</summary>
    public required ProbeAssertionKind Kind { get; init; }

    /// <summary>
    /// The right-hand-side value, string-typed on the wire. Coerced per
    /// <see cref="Kind"/>: an integer status for <see cref="ProbeAssertionKind.Status"/>,
    /// a millisecond budget for <see cref="ProbeAssertionKind.LatencyBelowMs"/>,
    /// a substring for <see cref="ProbeAssertionKind.BodyContains"/>.
    /// </summary>
    public required string Expected { get; init; }

    /// <summary>Evaluate this assertion against a probe execution result.</summary>
    public ProbeAssertionVerdict Evaluate(ProbeExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Kind switch
        {
            ProbeAssertionKind.Status => Check(
                int.TryParse(Expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var want) && result.Status == want,
                $"status {result.Status} == {Expected}"),
            ProbeAssertionKind.LatencyBelowMs => Check(
                double.TryParse(Expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var budget) && result.LatencyMs <= budget,
                $"latency {result.LatencyMs:0.#}ms <= {Expected}ms"),
            ProbeAssertionKind.BodyContains => Check(
                result.Body is not null && result.Body.Contains(Expected, StringComparison.Ordinal),
                $"body contains '{Expected}'"),
            _ => new ProbeAssertionVerdict(false, $"unknown assertion kind {Kind}"),
        };
    }

    private static ProbeAssertionVerdict Check(bool passed, string description)
        => new(passed, description);
}

/// <summary>The kinds of predicate v1 supports.</summary>
public enum ProbeAssertionKind
{
    /// <summary>Response status equals the expected integer.</summary>
    Status = 0,
    /// <summary>Response latency is at or below the expected millisecond budget.</summary>
    LatencyBelowMs = 1,
    /// <summary>Response body contains the expected substring.</summary>
    BodyContains = 2,
}

/// <summary>One assertion's verdict — did it pass, and a human-readable why.</summary>
public sealed record ProbeAssertionVerdict(bool Passed, string Description);
