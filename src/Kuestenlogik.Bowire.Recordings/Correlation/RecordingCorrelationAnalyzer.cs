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
/// One key is not always enough (#545): a transaction that renames its
/// identifier on the way across the landscape lights only the lanes that
/// speak the chosen key. So a step the key missed gets a second and last
/// edge — it joins the transaction when it shares a <em>distinctive</em>
/// id-shaped value with a step the key did match, and the resulting
/// <see cref="RecordingCorrelationLink"/> always names that value.
/// See <see cref="JoinThroughBridges"/> for the admissibility rule and
/// what it turns down.
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
    /// How improbable a value has to be before it may bridge two steps
    /// (#545). A bridge is an <em>inference</em>, not the operator's
    /// choice, and unlike the seed key it gets no corroboration from a
    /// matching field name — so the value has to carry its own weight.
    /// Anything shorter than this has too few possible values for a
    /// collision to be surprising: <c>1</c>, <c>42</c>, <c>true</c> and
    /// <c>OK</c> are not evidence, and a harbour capture where
    /// <c>portCallId</c>, <c>craneId</c> and a dock number are all
    /// <c>1</c> is precisely the case that must not join.
    /// </summary>
    public const int MinBridgeValueLength = 6;

    /// <summary>
    /// The largest share of a recording's steps a value may appear on and
    /// still be treated as identifying ONE transaction.
    /// </summary>
    /// <remarks>
    /// Length alone is the wrong measure of "distinctive", and dangerously
    /// so: a session, tenant, customer or node id is long, high-entropy and
    /// sits on the same field name at both ends — the exact profile the
    /// strength score rewards most. A GUID session id would therefore be the
    /// single best bridge this analyzer can find, and it would fuse every
    /// request made in that session into one "transaction". A password
    /// change would become part of an order.
    /// <para>
    /// What separates a transaction key from a context key is not how it
    /// looks but how far it spreads: the transaction id appears on the steps
    /// belonging to that transaction, the session id appears on nearly
    /// everything. A value carried by most of the recording is describing
    /// the capture, not a transaction inside it.
    /// </para>
    /// <para>
    /// Deliberately generous — this rejects the pathological case without
    /// second-guessing short recordings, where a legitimate id can easily
    /// touch half the steps.
    /// </para>
    /// </remarks>
    public const double MaxBridgeCarrierShare = 0.6;

    /// <summary>
    /// Edges the join may walk, counting the seed match as the first.
    /// Fixed, not configurable: an unbounded walk over an id-rich
    /// recording relates everything to everything, which is worse than
    /// no join at all. Concretely — a step the key matched directly may
    /// bridge one hop further, and a step reached through a bridge never
    /// bridges onward.
    /// </summary>
    public const int MaxJoinDepth = 2;

    /// <summary>Dark lanes named in the "why did this not join" warning before it turns into a list.</summary>
    private const int MaxRejectedBridgesReported = 3;

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
            var frames = BuildFrames(step, offset, KeyFrameVerdict(resolved, match));

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

        // Second and last edge (#545). Runs after the whole first pass,
        // because a bridge can only be measured once every step has its
        // seed verdict — and it rewrites `events` in place, so lanes and
        // counts below already see the joined result.
        JoinThroughBridges(steps, offsets, events, resolved, warnings);

        var lanes = BuildLanes(events, warnings);
        var matchedSteps = events.Count(e => !string.Equals(e.Match, RecordingCorrelationMatch.None, StringComparison.Ordinal));
        var matchedProtocols = events
            .Where(e => !string.Equals(e.Match, RecordingCorrelationMatch.None, StringComparison.Ordinal))
            .Select(e => e.Protocol)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var derivedSteps = events.Count(e => string.Equals(e.Match, RecordingCorrelationMatch.Derived, StringComparison.Ordinal));
        var derivedProtocols = events
            .Where(e => string.Equals(e.Match, RecordingCorrelationMatch.Derived, StringComparison.Ordinal))
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
            warnings,
            derivedSteps,
            derivedProtocols);
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

    /// <summary>
    /// Place every received frame of a step on the axis and let
    /// <paramref name="verdict"/> decide its tier. The verdict is a
    /// parameter rather than being derived here because the same frame
    /// list is built twice for a derived step (#545) — once against the
    /// key, once against the bridge value that actually joined it.
    /// </summary>
    private static List<RecordingCorrelationFrame> BuildFrames(
        BowireRecordingStep step,
        long stepOffset,
        Func<BowireRecordingFrame, string> verdict)
    {
        var received = step.ReceivedMessages;
        if (received is null || received.Count == 0) return [];

        var frames = new List<RecordingCorrelationFrame>(received.Count);
        for (var i = 0; i < received.Count; i++)
        {
            var frame = received[i];
            if (frame is null) continue;
            var offset = stepOffset + Math.Max(0, frame.TimestampMs ?? 0);

            frames.Add(new RecordingCorrelationFrame(
                frame.Index >= 0 ? frame.Index : i,
                offset,
                verdict(frame),
                string.IsNullOrWhiteSpace(frame.Discriminator) || frame.Discriminator == "*"
                    ? null
                    : frame.Discriminator));
        }
        return frames;
    }

    /// <summary>Per-frame verdict against the resolved correlation key.</summary>
    private static Func<BowireRecordingFrame, string> KeyFrameVerdict(
        RecordingCorrelationKey? key,
        string stepMatch)
    {
        if (key is null) return _ => RecordingCorrelationMatch.None;

        // Headers are step-level metadata — every frame of a matched
        // step inherits the step's verdict rather than pretending the
        // frame body carried the header.
        if (string.Equals(key.Source, RecordingCorrelationKey.SourceHeader, StringComparison.Ordinal))
        {
            return _ => stepMatch;
        }

        var keyName = RecordingCorrelationScanner.NormalizeName(key.Name);
        return frame => MatchFrame(frame, keyName, key.Value);
    }

    /// <summary>
    /// Per-frame verdict against a bridge value (#545). Only a frame
    /// that carries the value itself lights up, so a derived streaming
    /// lane never renders a lit bar over dead ticks.
    /// </summary>
    private static Func<BowireRecordingFrame, string> BridgeFrameVerdict(string bridgeValue)
        => frame =>
        {
            var carried = false;
            RecordingCorrelationScanner.ScanFrame(frame, (_, normalized, value) =>
            {
                if (carried) return;
                if (!string.Equals(value, bridgeValue, StringComparison.Ordinal)) return;
                if (normalized.EndsWith("id", StringComparison.Ordinal)) carried = true;
            });
            return carried ? RecordingCorrelationMatch.Derived : RecordingCorrelationMatch.None;
        };

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

    // ---- The second edge (#545) ----
    //
    // A transaction that renames its identifier on the way across the
    // landscape lights only the lanes that speak the seed key. The harbor
    // recording is the exact case: keyed on shipId=101, the GraphQL step
    // stays dark — not for lack of a key, but because it calls the same
    // transaction portCall.id=1. The bridge is already in the data, in
    // the container ids it shares with the REST step.
    //
    // Which shared values count as evidence is the whole problem. Two
    // steps sharing `1` is near-worthless; sharing `MSCU1234567` is
    // strong. Three gates decide it, and all three must hold.

    /// <summary>
    /// Join the steps the key left dark onto the transaction through a
    /// value they share with a step it lit (#545). Rewrites
    /// <paramref name="events"/> in place — a joined step becomes
    /// <see cref="RecordingCorrelationMatch.Derived"/> and always carries
    /// the <see cref="RecordingCorrelationLink"/> that explains it.
    /// </summary>
    private static void JoinThroughBridges(
        List<BowireRecordingStep> steps,
        long[] offsets,
        List<RecordingCorrelationEvent> events,
        RecordingCorrelationKey? key,
        List<string> warnings)
    {
        if (key is null || events.Count < 2) return;

        // Frozen BEFORE anything is joined: only a step the seed key
        // matched may act as a bridge source. That one line is what fixes
        // the walk at MaxJoinDepth edges, and it also makes the result
        // independent of the order the dark steps happen to be visited
        // in — a step joined early can never become a stepping stone for
        // a step joined later.
        // STRONG anchors only. A weak match is this analyzer's own name for
        // a coincidence — same value on some other id-shaped leaf, no
        // agreement on the field name, and no length floor — so a step lit
        // by a bare "1" could otherwise drag an unrelated step onto the
        // transaction and present it as evidence. A bridge already gets no
        // name corroboration of its own; anchoring it to something that has
        // none either leaves the far end resting on nothing.
        // Two different questions, deliberately two arrays: who may ANCHOR a
        // bridge, and who is still dark enough to NEED one. A weakly matched
        // step is neither — it is already on the timeline, and it is not
        // solid enough to pull anyone else on.
        var seedLit = new bool[events.Count];
        var anySeedLit = false;
        var anyDark = false;
        for (var i = 0; i < events.Count; i++)
        {
            var match = events[i].Match;
            seedLit[i] = string.Equals(match, RecordingCorrelationMatch.Strong, StringComparison.Ordinal);
            anySeedLit |= seedLit[i];
            anyDark |= string.Equals(match, RecordingCorrelationMatch.None, StringComparison.Ordinal);
        }
        if (!anySeedLit || !anyDark) return;

        var index = BuildLeafIndex(steps);
        var rejected = new List<string>();

        for (var i = 0; i < events.Count; i++)
        {
            if (seedLit[i]) continue;

            var link = FindBridge(i, index, seedLit, events, out var nearMiss);
            if (link is not null)
            {
                events[i] = events[i] with
                {
                    Match = RecordingCorrelationMatch.Derived,
                    Link = link,
                    Frames = BuildFrames(steps[i], offsets[i], BridgeFrameVerdict(link.Value)),
                };
                continue;
            }

            if (nearMiss is not null)
            {
                rejected.Add($"{events[i].Protocol} ({nearMiss.Value.Name} = {nearMiss.Value.Value})");
            }
        }

        if (rejected.Count == 0) return;

        // An operator staring at a dark lane will ask why. Saying nothing
        // is the one answer that makes the join look arbitrary, so name
        // the candidate that was considered and turned down.
        var named = string.Join(", ", rejected.Take(MaxRejectedBridgesReported));
        var tail = rejected.Count > MaxRejectedBridgesReported
            ? $", and {RecordingCorrelationScanner.Format(rejected.Count - MaxRejectedBridgesReported)} more"
            : string.Empty;
        warnings.Add(
            $"{RecordingCorrelationScanner.Format(rejected.Count)} step(s) share a value with the correlated "
            + $"steps but were not joined, because the shared value is too weak to be evidence: {named}{tail}. "
            + $"A bridge value must be id-shaped on both steps, at least {MinBridgeValueLength} characters long, "
            + "never carried by a non-id field, on a minority of the recording's steps, and its two field "
            + "names must be the same identifier under two spellings. The step it bridges to must be a "
            + "strong match, not a weak one.");
    }

    /// <summary>
    /// The best admissible bridge for one dark step, or
    /// <see langword="null"/> when nothing it carries is evidence.
    /// </summary>
    /// <param name="darkIndex">Index of the step the key did not match.</param>
    /// <param name="index">The one-walk leaf index for this recording.</param>
    /// <param name="seedLit">Which steps the seed key matched, frozen before any join.</param>
    /// <param name="events">First-pass events, read for the bridge step's identity.</param>
    /// <param name="nearMiss">
    /// The first value this step genuinely shares with a matched step and
    /// which was nonetheless turned down. Feeds the warning that tells an
    /// operator the lane was considered rather than overlooked.
    /// </param>
    private static RecordingCorrelationLink? FindBridge(
        int darkIndex,
        LeafIndex index,
        bool[] seedLit,
        List<RecordingCorrelationEvent> events,
        out (string Name, string Value)? nearMiss)
    {
        nearMiss = null;
        RecordingCorrelationLink? best = null;
        var bestStrength = int.MinValue;
        var admissibleValues = new List<string>();

        // Scan order, not hash order: three container ids tie exactly on
        // the harbor recording, and the CLI's `via` column and the UI chip
        // have to name the same one on every run.
        foreach (var leaf in index.IdLeaves[darkIndex])
        {
            var names = index.NamesByValue[leaf.Value];
            var carriers = index.IdCarriers[leaf.Value];

            // GATE 2 — distinctiveness.
            // (GATE 1, id-shape, already applies: IdLeaves only holds
            // leaves whose name ends in "id", and IdCarriers only records
            // the same, so both ends of every edge are id-shaped.)
            // GATE 4 — selectivity. See MaxBridgeCarrierShare: a value on
            // most of the steps is describing the capture (a session, a
            // tenant, a node), not a transaction within it.
            // GATE 3 (name cohesion) is NOT applied here any more — it is
            // now checked per edge below, against the two names actually
            // involved. Checked over every carrier in the recording it was
            // both toothless and non-monotonic: whenever any occurrence sat
            // on a field literally called "id" the family root became "id",
            // which every id-suffixed name satisfies by definition, and
            // appending an unrelated step could flip an already-rejected
            // bridge to accepted.
            // Two carriers is the FLOOR, not a smell: a bridge needs the dark
            // step at one end and the lit step at the other, so it always
            // touches at least two. On a three-step recording that is already
            // 67% — a share test alone would reject every bridge a short
            // capture can produce. The floor is what keeps the gate aimed at
            // the case it exists for: a value smeared across most of a long
            // recording.
            var distinctCarrierSteps = carriers.Select(c => c.StepIndex).Distinct().Count();
            var carrierCeiling = Math.Max(2, events.Count * MaxBridgeCarrierShare);
            var tooCommon = distinctCarrierSteps > carrierCeiling;

            // GATE 6 — a value doing two jobs is a label, not an identifier.
            // "Loading" on both `statusId` and `status` is an enum the whole
            // capture shares, not something that identifies one transaction.
            // Unlike the old whole-recording cohesion check this only ever
            // REJECTS as more of the recording is considered, so a later step
            // can never talk an unsound bridge into being accepted.
            var wearsANonIdName = names.Any(n => !n.EndsWith("id", StringComparison.Ordinal));

            if (leaf.Value.Length < MinBridgeValueLength || tooCommon || wearsANonIdName)
            {
                if (nearMiss is null
                    && carriers.Any(c => c.StepIndex != darkIndex && seedLit[c.StepIndex]))
                {
                    nearMiss = (leaf.Name, leaf.Value);
                }
                continue;
            }

            var admitted = false;
            foreach (var carrier in carriers)
            {
                // GATE 5 — depth. Seed-lit steps only.
                if (carrier.StepIndex == darkIndex || !seedLit[carrier.StepIndex]) continue;

                // GATE 3 — name cohesion, per edge. The two names actually
                // being joined must be the same entity under two spellings:
                // identical, or one a suffix of the other (id / shipId /
                // onShipId). Judged on this pair alone, so the verdict does
                // not change when an unrelated step elsewhere in the
                // recording happens to reuse the value.
                if (!NamesCohere(leaf.NormalizedName, carrier.NormalizedName)) continue;

                admitted = true;

                var strength = BridgeStrength(leaf.NormalizedName, carrier.NormalizedName, leaf.Value, names.Count);
                if (strength <= bestStrength) continue;
                var via = events[carrier.StepIndex];
                bestStrength = strength;
                best = new RecordingCorrelationLink(
                    leaf.Value,
                    leaf.Name,
                    via.StepId,
                    via.StepIndex,
                    via.Protocol,
                    carrier.Name,
                    via.Service,
                    via.Method,
                    0,
                    strength);
            }

            if (admitted && !admissibleValues.Contains(leaf.Value, StringComparer.Ordinal))
            {
                admissibleValues.Add(leaf.Value);
            }
        }

        return best is null ? null : best with { AlternativeCount = admissibleValues.Count - 1 };
    }

    /// <summary>
    /// GATE 3 — the two field names being joined must be one entity under
    /// two spellings: identical, or the shorter a suffix of the longer.
    /// <c>id</c>/<c>onShipId</c> and <c>shipId</c>/<c>occupiedByShipId</c>
    /// cohere; <c>craneId</c>/<c>portCallId</c> do not.
    /// </summary>
    /// <remarks>
    /// Judged on the candidate edge alone, deliberately. The earlier version
    /// took every carrier of the value anywhere in the recording and looked
    /// for a common suffix root, which failed twice over: the root collapsed
    /// to <c>"id"</c> as soon as any occurrence sat on a bare <c>id</c>
    /// field — and then every id-suffixed name satisfies it, so the gate
    /// stopped rejecting anything precisely in the commonest case — and the
    /// verdict depended on steps that had nothing to do with the edge, so
    /// appending an unrelated step could flip a rejected bridge to accepted.
    /// Both names are already known to end in <c>id</c> by gate 1.
    /// </remarks>
    private static bool NamesCohere(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;

        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return longer.EndsWith(shorter, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rank among admissible bridges. Same leaf name on both ends is the
    /// strongest reading (the two services agree on what the field is
    /// called); a longer value is less likely to collide; a value spread
    /// across many field names is doing more than one job and is
    /// penalised for it.
    /// </summary>
    private static int BridgeStrength(string darkName, string litName, string value, int nameSpread)
        => (string.Equals(darkName, litName, StringComparison.Ordinal) ? 200 : 100)
            + Math.Min(value.Length, 32)
            - (8 * (nameSpread - 1));

    /// <summary>
    /// One walk over every payload, producing what the join needs: the
    /// id-shaped leaves of each step in scan order, every field name each
    /// value is carried by anywhere in the recording, and which steps
    /// carry a value on an id-shaped leaf.
    ///
    /// <para>
    /// Deliberately separate from <see cref="Suggest"/> rather than
    /// feeding it: candidate scoring must not see derived edges, or the
    /// suggestion list would drift as the join grows.
    /// </para>
    /// </summary>
    private static LeafIndex BuildLeafIndex(List<BowireRecordingStep> steps)
    {
        var index = new LeafIndex();
        for (var i = 0; i < steps.Count; i++)
        {
            var stepIndex = i;
            var leaves = new List<IdLeaf>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            RecordingCorrelationScanner.ScanStep(steps[i], (name, normalized, value) =>
            {
                if (!index.NamesByValue.TryGetValue(value, out var names))
                {
                    names = [];
                    index.NamesByValue[value] = names;
                }
                if (!names.Contains(normalized, StringComparer.Ordinal)) names.Add(normalized);

                if (!normalized.EndsWith("id", StringComparison.Ordinal)) return;
                if (!seen.Add(normalized + "\0" + value)) return;

                leaves.Add(new IdLeaf(name, normalized, value));
                if (!index.IdCarriers.TryGetValue(value, out var carriers))
                {
                    carriers = [];
                    index.IdCarriers[value] = carriers;
                }
                carriers.Add(new IdCarrier(stepIndex, name, normalized));
            });
            index.IdLeaves.Add(leaves);
        }
        return index;
    }

    private readonly record struct IdLeaf(string Name, string NormalizedName, string Value);

    private readonly record struct IdCarrier(int StepIndex, string Name, string NormalizedName);

    private sealed class LeafIndex
    {
        /// <summary>Per step, its distinct id-shaped leaves in scan order.</summary>
        public List<List<IdLeaf>> IdLeaves { get; } = [];

        /// <summary>Value to every distinct normalised field name carrying it, id-shaped or not.</summary>
        public Dictionary<string, List<string>> NamesByValue { get; } = new(StringComparer.Ordinal);

        /// <summary>Value to the id-shaped leaves carrying it, one entry per (step, name).</summary>
        public Dictionary<string, List<IdCarrier>> IdCarriers { get; } = new(StringComparer.Ordinal);
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
            if (string.Equals(e.Match, RecordingCorrelationMatch.Derived, StringComparison.Ordinal))
            {
                lane.DerivedCount++;
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
            .Select(l => new RecordingCorrelationLane(l.Protocol, l.StepCount, l.MatchedCount, l.DerivedCount))
            .ToList();
    }

    private sealed class LaneAccumulator(string protocol)
    {
        public string Protocol { get; } = protocol;
        public int StepCount { get; set; }
        public int MatchedCount { get; set; }
        public int DerivedCount { get; set; }
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
        var groupKey = source + "\0" + normalizedName + "\0" + value;
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
