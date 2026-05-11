// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Semantics.Detectors;

/// <summary>
/// Small helper that wraps "decode this frame's JSON + hand it to
/// the prober" for the SSE stream endpoint
/// (<c>BowireInvokeEndpoints.MapBowireInvokeEndpoints</c>). Keeps the
/// endpoint file unchanged in shape — one helper call per frame —
/// and isolates the parsing failure-modes so a malformed frame
/// can't take the stream down.
/// </summary>
/// <remarks>
/// <para>
/// In Phase 2 we don't have plugin-side discriminator wiring yet, so
/// every frame is observed with <see cref="AnnotationKey.Wildcard"/>
/// as the message-type. Phase 3 will plumb the discriminator value
/// through.
/// </para>
/// </remarks>
internal static class FrameProbingMiddleware
{
    /// <summary>
    /// Decode <paramref name="frameJson"/> and feed the resulting
    /// <see cref="JsonElement"/> to
    /// <paramref name="prober"/>.<see cref="IFrameProber.ObserveFrame"/>.
    /// No-ops when <paramref name="prober"/> is <c>null</c>, when
    /// <paramref name="frameJson"/> is empty, or when the JSON is
    /// malformed — the prober is a side-channel, never a stream-
    /// breaking concern.
    /// </summary>
    public static void Observe(
        IFrameProber? prober, string service, string method, string? frameJson)
    {
        if (prober is null) return;
        if (string.IsNullOrEmpty(frameJson)) return;

        JsonDocument? doc;
        try
        {
            doc = JsonDocument.Parse(frameJson);
        }
        catch (JsonException)
        {
            // Frame isn't valid JSON — protocol plugin must have
            // emitted a raw scalar / binary blob. Phase-2 detectors
            // operate on JSON; non-JSON frames silently skip the
            // probe.
            return;
        }

        try
        {
            var ctx = new DetectionContext(
                ServiceId: service,
                MethodId: method,
                MessageType: AnnotationKey.Wildcard,
                Frame: doc.RootElement);
            prober.ObserveFrame(in ctx);
        }
        finally
        {
            doc.Dispose();
        }
    }
}
