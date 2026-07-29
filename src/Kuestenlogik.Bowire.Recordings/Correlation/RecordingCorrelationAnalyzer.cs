// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Recordings.Correlation;

/// <summary>
/// Turns a recording into a correlated timeline (#539) — the single
/// canonical implementation behind both the workbench's Timeline tab
/// (via <c>POST /api/recordings/correlate</c>) and
/// <c>bowire recording correlate</c>.
///
/// <para>
/// A recording has no trace id: nothing in the <c>.bwr</c> format
/// carries one, and no capture path writes one. So the analyzer resolves
/// a signal in three tiers — a correlation header on
/// <c>step.metadata</c>, otherwise a shared id-shaped JSON leaf inferred
/// from the payloads, otherwise nothing, in which case the whole
/// recording is treated as one transaction and the result is a plain
/// protocol-lane time chart.
/// </para>
///
/// <para>
/// Pure and stateless by contract: no cache, no ring buffer, no
/// injected dependency. Two calls with the same inputs produce the same
/// output, which is what lets the CLI and the endpoint agree.
/// </para>
/// </summary>
public static class RecordingCorrelationAnalyzer
{
    /// <summary>
    /// Score floor for a header candidate. Any correlation header beats
    /// every inferred field candidate, because a header is an explicit
    /// statement by the producer while a shared id is an inference.
    /// Field scores are <c>protocols * 1000 + steps</c>, which cannot
    /// realistically approach this, so the ordering is total without
    /// clamping at <c>int.MaxValue</c> (which would make several header
    /// candidates indistinguishable from each other).
    /// </summary>
    public const int HeaderScoreBase = 1_000_000;

    /// <summary>
    /// Frames per lane past which the timeline stops being readable and
    /// the renderer thins its ticks. Reported as a warning rather than
    /// silently truncating the model — the CLI still prints everything.
    /// </summary>
    public const int LaneFrameWarningThreshold = 200;

    /// <summary>Most suggestions we hand back; beyond this the picker stops being a picker.</summary>
    private const int MaxSuggestions = 20;

    /// <summary>
    /// Every key this recording would accept, best first. Empty when no
    /// value is shared by at least two steps.
    /// </summary>
    public static IReadOnlyList<RecordingCorrelationCandidate> Suggest(BowireRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        var groups = new Dictionary<string, CandidateGroup>(StringComparer.Ordinal);

        for (var i = 0; i < recording.Steps.Count; i++)
        {
            var step = recording.Steps[i];
            if (step is null) continue;
            var protocol = LaneOf(step);

            if (RecordingCorrelationScanner.TryReadCorrelationHeader(step.Metadata, out var hName, out var hValue))
            {
                Accumulate(groups, RecordingCorrelationKey.SourceHeader,
                    hName, RecordingCorrelationScanner.NormalizeName(hName), hValue, protocol, i);
            }

            RecordingCorrelationScanner.ScanStep(step, (name, normalized, value) =>
            {
                // Only id-shaped leaves are candidates. A name that IS
                // just "id" is too generic to suggest — `id` collides
                // across every entity in a multi-service capture — but
                // it stays selectable by hand, and it still counts as a
                // weak match once another key is chosen.
                if (!normalized.EndsWith("id", StringComparison.Ordinal)) return;
                if (string.Equals(normalized, "id", StringComparison.Ordinal)) return;
                Accumulate(groups, RecordingCorrelationKey.SourceField,
                    name, normalized, value, protocol, i);
            });
        }

        return groups.Values
            .Where(g => g.StepIndices.Count >= 2)
            .Select(g => new RecordingCorrelationCandidate(
                g.DisplayName,
                g.Value,
                g.Source,
                g.Protocols,
                g.StepIndices.Count,
                Score(g)))
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.StepCount)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.Value, StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .ToList();
    }

    /// <summary>
    /// Place every step (and every streamed frame) of the recording on a
    /// shared time axis and verdict each one against the resolved key.
    /// </summary>
    /// <param name="recording">The recording to analyse.</param>
    /// <param name="key">
    /// An explicit key from the operator or from CI. When
    /// <see langword="null"/>, the recording's persisted
    /// <see cref="BowireRecording.Correlation"/> wins, then the
    /// top-scoring suggestion, then nothing.
    /// </param>
    public static RecordingCorrelationTimeline Analyze(
        BowireRecording recording,
        RecordingCorrelationKey? key = null)
    {
        ArgumentNullException.ThrowIfNull(recording);

        var suggestions = Suggest(recording);
        var resolved = ResolveKey(recording, key, suggestions);
        var warnings = new List<string>();

        var steps = recording.Steps.Where(s => s is not null).ToList();
        var offsets = ComputeOffsets(steps, out var originMs, out var relativeFallback);
        if (relativeFallback && steps.Count > 0)
        {
            warnings.Add(
                "Every step carries capturedAt = 0, so offsets fall back to cumulative durations: "
                + "the axis shows call order and elapsed work, not real wall-clock spacing.");
        }

        var events = new List<RecordingCorrelationEvent>(steps.Count);
        long spanMs = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var offset = offsets[i];
            var match = MatchStep(step, resolved);
            var frames = BuildFrames(step, offset, resolved, match);

            events.Add(new RecordingCorrelationEvent(
                string.IsNullOrEmpty(step.Id) ? "step_" + i.ToString(CultureInfo.InvariantCulture) : step.Id,
                i,
                LaneOf(step),
                step.Service ?? string.Empty,
                step.Method ?? string.Empty,
                string.IsNullOrEmpty(step.MethodType) ? "Unary" : step.MethodType,
                string.IsNullOrEmpty(step.Status) ? "OK" : step.Status,
                offset,
                Math.Max(0, step.DurationMs),
                match,
                frames));

            spanMs = Math.Max(spanMs, offset + Math.Max(0, step.DurationMs));
            foreach (var frame in frames) spanMs = Math.Max(spanMs, frame.OffsetMs);
        }

        var lanes = BuildLanes(events, warnings);
        var matchedSteps = events.Count(e => !string.Equals(e.Match, RecordingCorrelationMatch.None, StringComparison.Ordinal));
        var matchedProtocols = events
            .Where(e => !string.Equals(e.Match, RecordingCorrelationMatch.None, StringComparison.Ordinal))
            .Select(e => e.Protocol)
            .Distinct(StringComparer.Ordinal)
            .Count();

        if (resolved is not null && matchedSteps == 0)
        {
            warnings.Add(
                $"Key '{resolved.Name}={resolved.Value}' matched no step. "
                + "The lanes below are still a valid time chart, but nothing ties them together.");
        }

        // A relative timebase is the authored-sample case (capturedAt 0,
        // 150, 300, …); an absolute one is a live capture stamped with
        // Date.now(). Renderers key off this to decide whether printing
        // a wall-clock time would be a lie.
        var timebase = originMs >= 1_000_000_000_000L
            ? RecordingCorrelationTimeline.TimebaseAbsolute
            : RecordingCorrelationTimeline.TimebaseRelative;

        return new RecordingCorrelationTimeline(
            recording.Id ?? string.Empty,
            recording.Name ?? string.Empty,
            timebase,
            originMs,
            Math.Max(1, spanMs),
            resolved,
            suggestions,
            lanes,
            events,
            matchedSteps,
            matchedProtocols,
            warnings);
    }

    /// <summary>
    /// Decide where a key came from when the caller did not say. A name
    /// that reads as one of the recognised correlation headers is a
    /// header key; anything else is a payload leaf.
    /// </summary>
    public static string ResolveSource(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return RecordingCorrelationKey.SourceNone;
        var probe = new Dictionary<string, string>(StringComparer.Ordinal) { [name] = "probe" };
        return RecordingCorrelationScanner.TryReadCorrelationHeader(probe, out _, out _)
            ? RecordingCorrelationKey.SourceHeader
            : RecordingCorrelationKey.SourceField;
    }

    private static RecordingCorrelationKey? ResolveKey(
        BowireRecording recording,
        RecordingCorrelationKey? explicitKey,
        IReadOnlyList<RecordingCorrelationCandidate> suggestions)
    {
        if (explicitKey is not null
            && !string.IsNullOrWhiteSpace(explicitKey.Name)
            && !string.IsNullOrWhiteSpace(explicitKey.Value))
        {
            return Normalise(explicitKey.Name, explicitKey.Value, explicitKey.Source);
        }

        var persisted = recording.Correlation;
        if (persisted is not null
            && !string.IsNullOrWhiteSpace(persisted.Name)
            && !string.IsNullOrWhiteSpace(persisted.Value))
        {
            return Normalise(persisted.Name, persisted.Value, persisted.Source);
        }

        var top = suggestions.Count > 0 ? suggestions[0] : null;
        return top is null ? null : Normalise(top.Name, top.Value, top.Source);

        static RecordingCorrelationKey Normalise(string name, string value, string? source)
        {
            var src = source;
            if (string.IsNullOrWhiteSpace(src)
                || string.Equals(src, RecordingCorrelationKey.SourceNone, StringComparison.Ordinal))
            {
                src = ResolveSource(name);
            }
            return new RecordingCorrelationKey(name.Trim(), value.Trim(), src);
        }
    }

    private static string MatchStep(BowireRecordingStep step, RecordingCorrelationKey? key)
    {
        if (key is null) return RecordingCorrelationMatch.None;

        if (string.Equals(key.Source, RecordingCorrelationKey.SourceHeader, StringComparison.Ordinal))
        {
            // A header either carries the transaction id or it does not
            // — there is no honest "weak" tier for an explicit signal.
            return RecordingCorrelationScanner.TryReadCorrelationHeader(step.Metadata, out _, out var carried)
                && string.Equals(carried, key.Value, StringComparison.OrdinalIgnoreCase)
                    ? RecordingCorrelationMatch.Strong
                    : RecordingCorrelationMatch.None;
        }

        var keyName = RecordingCorrelationScanner.NormalizeName(key.Name);
        var keyValue = key.Value;
        var verdict = RecordingCorrelationMatch.None;
        RecordingCorrelationScanner.ScanStep(step, (_, normalized, value) =>
        {
            if (string.Equals(verdict, RecordingCorrelationMatch.Strong, StringComparison.Ordinal)) return;
            if (!string.Equals(value, keyValue, StringComparison.Ordinal)) return;
            if (keyName.Length > 0 && normalized.EndsWith(keyName, StringComparison.Ordinal))
            {
                verdict = RecordingCorrelationMatch.Strong;
                return;
            }
            // Same value on some *other* id-shaped leaf. Kept separate
            // because low-cardinality ids collide: in a harbour capture
            // portCallId=1, craneId=1 and dock 1 are three different
            // things wearing the same number.
            if (normalized.EndsWith("id", StringComparison.Ordinal))
            {
                verdict = RecordingCorrelationMatch.Weak;
            }
        });
        return verdict;
    }

    private static List<RecordingCorrelationFrame> BuildFrames(
        BowireRecordingStep step,
        long stepOffset,
        RecordingCorrelationKey? key,
        string stepMatch)
    {
        var received = step.ReceivedMessages;
        if (received is null || received.Count == 0) return [];

        var isHeaderKey = key is not null
            && string.Equals(key.Source, RecordingCorrelationKey.SourceHeader, StringComparison.Ordinal);
        var keyName = key is null ? string.Empty : RecordingCorrelationScanner.NormalizeName(key.Name);

        var frames = new List<RecordingCorrelationFrame>(received.Count);
        for (var i = 0; i < received.Count; i++)
        {
            var frame = received[i];
            if (frame is null) continue;
            var offset = stepOffset + Math.Max(0, frame.TimestampMs ?? 0);

            string match;
            if (key is null)
            {
                match = RecordingCorrelationMatch.None;
            }
            else if (isHeaderKey)
            {
                // Headers are step-level metadata — every frame of a
                // matched step inherits the step's verdict rather than
                // pretending the frame body carried the header.
                match = stepMatch;
            }
            else
            {
                match = MatchFrame(frame, keyName, key.Value);
            }

            frames.Add(new RecordingCorrelationFrame(
                frame.Index >= 0 ? frame.Index : i,
                offset,
                match,
                string.IsNullOrWhiteSpace(frame.Discriminator) || frame.Discriminator == "*"
                    ? null
                    : frame.Discriminator));
        }
        return frames;
    }

    private static string MatchFrame(BowireRecordingFrame frame, string keyName, string keyValue)
    {
        var verdict = RecordingCorrelationMatch.None;
        RecordingCorrelationScanner.ScanFrame(frame, (_, normalized, value) =>
        {
            if (string.Equals(verdict, RecordingCorrelationMatch.Strong, StringComparison.Ordinal)) return;
            if (!string.Equals(value, keyValue, StringComparison.Ordinal)) return;
            if (keyName.Length > 0 && normalized.EndsWith(keyName, StringComparison.Ordinal))
            {
                verdict = RecordingCorrelationMatch.Strong;
                return;
            }
            if (normalized.EndsWith("id", StringComparison.Ordinal))
            {
                verdict = RecordingCorrelationMatch.Weak;
            }
        });
        return verdict;
    }

    private static List<RecordingCorrelationLane> BuildLanes(
        List<RecordingCorrelationEvent> events,
        List<string> warnings)
    {
        // Lane order is first-appearance order, not alphabetical — the
        // timeline reads top-to-bottom as "what the transaction touched
        // first".
        var order = new List<LaneAccumulator>();
        var byProtocol = new Dictionary<string, LaneAccumulator>(StringComparer.Ordinal);

        foreach (var e in events)
        {
            if (!byProtocol.TryGetValue(e.Protocol, out var lane))
            {
                lane = new LaneAccumulator(e.Protocol);
                byProtocol[e.Protocol] = lane;
                order.Add(lane);
            }
            lane.StepCount++;
            lane.FrameCount += e.Frames.Count;
            if (!string.Equals(e.Match, RecordingCorrelationMatch.None, StringComparison.Ordinal))
            {
                lane.MatchedCount++;
            }
        }

        foreach (var lane in order)
        {
            if (lane.FrameCount > LaneFrameWarningThreshold)
            {
                warnings.Add(
                    $"Lane '{lane.Protocol}' carries {RecordingCorrelationScanner.Format(lane.FrameCount)} frames; "
                    + $"the timeline thins its ticks past {LaneFrameWarningThreshold}.");
            }
        }

        return order
            .Select(l => new RecordingCorrelationLane(l.Protocol, l.StepCount, l.MatchedCount))
            .ToList();
    }

    private sealed class LaneAccumulator(string protocol)
    {
        public string Protocol { get; } = protocol;
        public int StepCount { get; set; }
        public int MatchedCount { get; set; }
        public int FrameCount { get; set; }
    }

    /// <summary>
    /// Offsets from the timeline origin, one per step. Recordings come
    /// in two flavours: live captures stamp <c>capturedAt</c> with
    /// <c>Date.now()</c>, authored samples use small relative numbers,
    /// and a few carry nothing at all. The first two normalise to
    /// <c>capturedAt - min(capturedAt)</c>; the third falls back to
    /// cumulative durations so the lanes still read as a sequence.
    /// </summary>
    private static long[] ComputeOffsets(List<BowireRecordingStep> steps, out long originMs, out bool durationFallback)
    {
        originMs = 0;
        durationFallback = false;
        var offsets = new long[steps.Count];
        if (steps.Count == 0) return offsets;

        var anyStamp = steps.Any(s => s.CapturedAt != 0);
        if (!anyStamp)
        {
            durationFallback = true;
            long running = 0;
            for (var i = 0; i < steps.Count; i++)
            {
                offsets[i] = running;
                running += Math.Max(0, steps[i].DurationMs);
            }
            return offsets;
        }

        // Whether a zero means "the very start" or "not recorded"
        // depends on the flavour, and getting this backwards silently
        // shifts the whole axis. An authored sample legitimately starts
        // at capturedAt 0 and counts up in small numbers; a live capture
        // stamps Date.now(), where a lone 0 is a hole, and honouring it
        // would drag the origin back to 1970 and squash every real step
        // into one pixel.
        var looksAbsolute = steps.Max(s => s.CapturedAt) >= 1_000_000_000_000L;
        originMs = looksAbsolute
            ? steps.Where(s => s.CapturedAt != 0).Min(s => s.CapturedAt)
            : steps.Min(s => s.CapturedAt);
        for (var i = 0; i < steps.Count; i++)
        {
            offsets[i] = looksAbsolute && steps[i].CapturedAt == 0
                ? 0
                : Math.Max(0, steps[i].CapturedAt - originMs);
        }
        return offsets;
    }

    private static string LaneOf(BowireRecordingStep step)
        => string.IsNullOrWhiteSpace(step.Protocol) ? "unknown" : step.Protocol;

    private static void Accumulate(
        Dictionary<string, CandidateGroup> groups,
        string source,
        string displayName,
        string normalizedName,
        string value,
        string protocol,
        int stepIndex)
    {
        if (normalizedName.Length == 0 || value.Length == 0) return;
        var groupKey = source + " " + normalizedName + " " + value;
        if (!groups.TryGetValue(groupKey, out var group))
        {
            group = new CandidateGroup(displayName, value, source);
            groups[groupKey] = group;
        }
        group.StepIndices.Add(stepIndex);
        if (!group.Protocols.Contains(protocol, StringComparer.Ordinal)) group.Protocols.Add(protocol);
    }

    // Spanning more protocols is the stronger signal — a value repeated
    // across five services is a transaction id, the same value repeated
    // five times inside one REST response is a foreign key.
    private static int Score(CandidateGroup group)
    {
        var baseScore = (group.Protocols.Count * 1000) + group.StepIndices.Count;
        return string.Equals(group.Source, RecordingCorrelationKey.SourceHeader, StringComparison.Ordinal)
            ? HeaderScoreBase + baseScore
            : baseScore;
    }

    private sealed class CandidateGroup(string displayName, string value, string source)
    {
        public string DisplayName { get; } = displayName;
        public string Value { get; } = value;
        public string Source { get; } = source;
        public List<string> Protocols { get; } = [];
        public HashSet<int> StepIndices { get; } = [];
    }
}
