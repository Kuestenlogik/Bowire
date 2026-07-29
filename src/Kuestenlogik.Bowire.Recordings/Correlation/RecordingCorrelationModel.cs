// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Recordings.Correlation;

/// <summary>
/// The resolved signal a correlated timeline is read through (#539).
/// </summary>
/// <param name="Name">
/// Header name or JSON leaf name. Compared case- and
/// separator-insensitively; leaf names additionally match by suffix, so
/// <c>shipId</c> ties <c>onShipId</c> and <c>OccupiedByShipId</c>
/// together without also fusing <c>craneId</c>.
/// </param>
/// <param name="Value">
/// The value identifying this one transaction. Always text — a JSON
/// number leaf compares by its invariant string form so protocols that
/// disagree about the type (<c>101</c> vs <c>"101"</c>) still correlate.
/// </param>
/// <param name="Source">
/// <c>"header"</c> (a correlation header carried in
/// <c>step.metadata</c>), <c>"field"</c> (a shared id-shaped payload
/// leaf), or <c>"none"</c> (no signal exists — the timeline degrades to
/// a pure protocol-lane time chart).
/// </param>
public sealed record RecordingCorrelationKey(string Name, string Value, string Source)
{
    /// <summary>Source tag for a key found in a correlation header.</summary>
    public const string SourceHeader = "header";

    /// <summary>Source tag for a key found as a shared JSON payload leaf.</summary>
    public const string SourceField = "field";

    /// <summary>Source tag used when no signal could be resolved at all.</summary>
    public const string SourceNone = "none";
}

/// <summary>
/// One key the analyzer would accept, offered to the operator so they
/// can override the auto-pick.
/// </summary>
/// <param name="Name">The leaf / header name, in its first-seen spelling.</param>
/// <param name="Value">The shared value.</param>
/// <param name="Source">
/// <see cref="RecordingCorrelationKey.SourceHeader"/> or
/// <see cref="RecordingCorrelationKey.SourceField"/>.
/// </param>
/// <param name="Protocols">Distinct protocols the candidate was seen on, in first-appearance order.</param>
/// <param name="StepCount">How many steps carry it.</param>
/// <param name="Score">
/// Ranking weight — higher wins. Header candidates outrank every field
/// candidate; among field candidates, spanning more protocols beats
/// appearing in more steps of the same protocol.
/// </param>
public sealed record RecordingCorrelationCandidate(
    string Name,
    string Value,
    string Source,
    IReadOnlyList<string> Protocols,
    int StepCount,
    int Score);

/// <summary>One streamed frame inside a correlated event.</summary>
/// <param name="Index">Frame index inside the step's <c>receivedMessages</c>.</param>
/// <param name="OffsetMs">Offset from the timeline origin — step offset plus the frame's own timestamp.</param>
/// <param name="Match">
/// <see cref="RecordingCorrelationMatch.Strong"/> /
/// <see cref="RecordingCorrelationMatch.Weak"/> /
/// <see cref="RecordingCorrelationMatch.None"/> for this frame alone.
/// </param>
/// <param name="Label">Optional short label (the frame's discriminator) for the tooltip.</param>
public sealed record RecordingCorrelationFrame(
    int Index,
    long OffsetMs,
    string Match,
    string? Label);

/// <summary>The three match tiers a step or frame can land in.</summary>
public static class RecordingCorrelationMatch
{
    /// <summary>The key's own name (by suffix) and value were both found.</summary>
    public const string Strong = "strong";

    /// <summary>
    /// The value was found on some other id-shaped leaf. Kept visibly
    /// distinct because low-cardinality ids (<c>1</c>) collide across
    /// unrelated entities.
    /// </summary>
    public const string Weak = "weak";

    /// <summary>The key does not appear in this step at all.</summary>
    public const string None = "none";
}

/// <summary>One recorded step, placed on the shared time axis.</summary>
/// <param name="StepId">The step's own id.</param>
/// <param name="StepIndex">Zero-based position in the recording.</param>
/// <param name="Protocol">Lane this event belongs to.</param>
/// <param name="Service">Service identifier as recorded.</param>
/// <param name="Method">Method identifier as recorded.</param>
/// <param name="MethodType"><c>Unary</c> / <c>ServerStreaming</c> / <c>ClientStreaming</c> / <c>Duplex</c>.</param>
/// <param name="Status">Recorded status string.</param>
/// <param name="OffsetMs">Offset from the timeline origin.</param>
/// <param name="DurationMs">Recorded wall-clock duration.</param>
/// <param name="Match">Match tier for the whole step.</param>
/// <param name="Frames">Per-frame ticks for streaming steps; empty for unary.</param>
public sealed record RecordingCorrelationEvent(
    string StepId,
    int StepIndex,
    string Protocol,
    string Service,
    string Method,
    string MethodType,
    string Status,
    long OffsetMs,
    long DurationMs,
    string Match,
    IReadOnlyList<RecordingCorrelationFrame> Frames);

/// <summary>One protocol lane.</summary>
/// <param name="Protocol">Lane key — the step's <c>protocol</c> field.</param>
/// <param name="StepCount">Steps in this lane.</param>
/// <param name="MatchedCount">Steps in this lane that matched (strong or weak).</param>
public sealed record RecordingCorrelationLane(string Protocol, int StepCount, int MatchedCount);

/// <summary>
/// The full correlated view of one recording — what the workbench's
/// Timeline tab renders and what <c>bowire recording correlate --json</c>
/// prints.
/// </summary>
/// <param name="RecordingId">Id of the analysed recording.</param>
/// <param name="RecordingName">Its display name.</param>
/// <param name="Timebase">
/// <c>"absolute"</c> when the recording carries wall-clock
/// <c>capturedAt</c> stamps, <c>"relative"</c> when they are offsets
/// from an arbitrary zero (which is what authored sample recordings
/// use). Renderers must not print a wall-clock date for
/// <c>"relative"</c>.
/// </param>
/// <param name="OriginMs">The <c>capturedAt</c> every offset is measured from.</param>
/// <param name="SpanMs">Total width of the timeline, always at least 1.</param>
/// <param name="Key">The resolved key, or <see langword="null"/> when no signal exists.</param>
/// <param name="Suggestions">Every key the analyzer would accept, best first.</param>
/// <param name="Lanes">One entry per protocol, in first-appearance order.</param>
/// <param name="Events">One entry per step, in recording order.</param>
/// <param name="MatchedStepCount">Steps whose match is not <c>none</c>.</param>
/// <param name="MatchedProtocolCount">Distinct protocols with at least one matched step.</param>
/// <param name="Warnings">Human-readable caveats about this particular analysis.</param>
public sealed record RecordingCorrelationTimeline(
    string RecordingId,
    string RecordingName,
    string Timebase,
    long OriginMs,
    long SpanMs,
    RecordingCorrelationKey? Key,
    IReadOnlyList<RecordingCorrelationCandidate> Suggestions,
    IReadOnlyList<RecordingCorrelationLane> Lanes,
    IReadOnlyList<RecordingCorrelationEvent> Events,
    int MatchedStepCount,
    int MatchedProtocolCount,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Timebase tag for recordings whose <c>capturedAt</c> are wall-clock epoch milliseconds.</summary>
    public const string TimebaseAbsolute = "absolute";

    /// <summary>Timebase tag for recordings whose <c>capturedAt</c> are offsets from an arbitrary zero.</summary>
    public const string TimebaseRelative = "relative";
}
