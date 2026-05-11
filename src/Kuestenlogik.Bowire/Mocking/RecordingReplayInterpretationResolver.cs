// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Semantics;
using Kuestenlogik.Bowire.Semantics.Detectors;

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// Phase-5 replay-determinism shim. Given a recording step (either the
/// top-level <see cref="BowireRecordingStep"/> or one of its
/// <see cref="BowireRecordingFrame"/> children) plus the frame about to
/// be emitted, decide whether to short-circuit detection with the
/// captured <see cref="RecordedInterpretation"/> list or fall back to
/// the live <see cref="IFrameProber"/> + builder pass.
/// </summary>
/// <remarks>
/// <para>
/// The rule the ADR's "Recording / replay integration" section pins:
/// </para>
/// <list type="number">
///   <item><description>
///   Step carries <c>interpretations</c> → use them verbatim. Skip the
///   prober. The replayed widget sees the same payload the original
///   capture did, regardless of how detector heuristics have drifted
///   since record-time.
///   </description></item>
///   <item><description>
///   Step has no <c>interpretations</c> field (pre-Phase-5 recordings) →
///   run the live prober + <see cref="RecordingInterpretationBuilder"/>
///   as a fall-back. Same behaviour as the live path.
///   </description></item>
/// </list>
/// <para>
/// The fall-back path is the v1-recording backwards-compatibility
/// guarantee: a recording captured under v1.2.0 (no
/// <c>interpretations</c>, no <c>discriminator</c>, no
/// <c>schemaSnapshot</c>) still replays cleanly through the workbench
/// under v1.3+. The prober only ticks for pre-Phase-5 frames — the
/// counter on the prober is the test seam that verifies the
/// short-circuit.
/// </para>
/// </remarks>
public static class RecordingReplayInterpretationResolver
{
    /// <summary>
    /// Resolve the interpretations to emit alongside a single replayed
    /// frame. Honours the captured list when present; otherwise runs
    /// detection via the supplied <paramref name="prober"/> + the
    /// effective annotation store, exactly as the live SSE path does.
    /// </summary>
    /// <param name="capturedInterpretations">
    /// The <c>interpretations</c> list on the recorded step / frame.
    /// <c>null</c> for pre-Phase-5 captures.
    /// </param>
    /// <param name="prober">
    /// Frame prober to feed the frame through when the recording carries
    /// no captured interpretations. May be <c>null</c> when the host has
    /// opted out of built-in detection (<c>DisableBuiltInDetectors</c>) —
    /// the fall-back path then returns an empty list, which is the
    /// correct "no widget mounts" semantics for an unannotated frame.
    /// </param>
    /// <param name="store">
    /// Annotation store used by the fall-back path to walk effective
    /// annotations and pair them with the frame. May be <c>null</c> when
    /// the host has no annotation store wired — also produces empty.
    /// </param>
    /// <param name="serviceId">Service identifier of the recorded step.</param>
    /// <param name="methodId">Method identifier of the recorded step.</param>
    /// <param name="messageType">
    /// Discriminator value for this frame —
    /// <see cref="AnnotationKey.Wildcard"/> when the recording predates
    /// Phase 5 (no <c>discriminator</c> field) and the framework treats
    /// the step as single-type.
    /// </param>
    /// <param name="frame">
    /// Decoded JSON payload rooted at the message body. The fall-back
    /// path needs this to extract typed payloads (lat/lon, base64 bytes,
    /// ...) for each effective annotation.
    /// </param>
    public static IReadOnlyList<RecordedInterpretation> Resolve(
        IReadOnlyList<RecordedInterpretation>? capturedInterpretations,
        IFrameProber? prober,
        IAnnotationStore? store,
        string serviceId,
        string methodId,
        string messageType,
        JsonElement frame)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(methodId);
        ArgumentNullException.ThrowIfNull(messageType);

        // Short-circuit — captured interpretations win, no detector pass.
        // The replay viewer sees exactly the payload the original capture
        // recorded, regardless of detector-heuristic drift.
        if (capturedInterpretations is not null)
        {
            return capturedInterpretations;
        }

        // Fall-back path for pre-Phase-5 captures: run the prober to
        // populate the auto-layer (same observe-once-per-triple semantics
        // the live SSE path uses) and then resolve interpretations from
        // the effective store.
        if (store is null)
        {
            return [];
        }

        if (prober is not null)
        {
            var ctx = new DetectionContext(
                ServiceId: serviceId,
                MethodId: methodId,
                MessageType: messageType,
                Frame: frame);
            prober.ObserveFrame(in ctx);
        }

        return RecordingInterpretationBuilder.Build(
            store, serviceId, methodId, messageType, frame);
    }
}
