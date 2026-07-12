// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Lighthouse;

/// <summary>
/// One row in a probe's outcome ledger — the durable record of a single run.
/// Serialised one-per-line into <c>&lt;probe&gt;.jsonl</c>; the last row is the
/// source of truth for restart resume + transition detection (Decision 2).
/// </summary>
public sealed record ProbeOutcome
{
    /// <summary>Run timestamp, Unix epoch milliseconds (UTC). The ledger's clock.</summary>
    [JsonPropertyName("t")]
    public required long TimestampUnixMs { get; init; }

    /// <summary>Pass / fail / error.</summary>
    [JsonPropertyName("result")]
    public required ProbeResult Result { get; init; }

    /// <summary>Measured latency in milliseconds (0 on error).</summary>
    [JsonPropertyName("latencyMs")]
    public double LatencyMs { get; init; }

    /// <summary>Per-assertion verdicts (empty on error).</summary>
    [JsonPropertyName("assertions")]
    public IReadOnlyList<ProbeAssertionVerdict> Assertions { get; init; } = [];

    /// <summary>Error detail when <see cref="Result"/> is <see cref="ProbeResult.Error"/>.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
