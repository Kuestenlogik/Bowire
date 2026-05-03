// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// The subset of mock-server configuration that
/// <see cref="IBowireMockEmitter"/> implementations actually consume.
/// Kept deliberately small so plugin authors don't have to take a
/// compile-time dependency on the full <c>MockOptions</c> shape —
/// which carries server-only concerns (matchers, chaos injection,
/// stateful-cursor bookkeeping) that don't apply to emitters.
/// </summary>
/// <remarks>
/// The mock server constructs an instance of this type from its own
/// <c>MockOptions</c> and passes it to every emitter's
/// <see cref="IBowireMockEmitter.StartAsync"/>. Downstream emitter
/// code reads <see cref="ReplaySpeed"/> and <see cref="Loop"/>
/// without any knowledge of the matchers / chaos knobs the server
/// is using.
/// </remarks>
public sealed class MockEmitterOptions
{
    /// <summary>
    /// Speed multiplier for streaming replay. <c>1.0</c> preserves the
    /// original cadence captured on the per-frame <c>timestampMs</c>;
    /// <c>2.0</c> is twice as fast; <c>0</c> emits every frame
    /// immediately. Non-positive values other than <c>0</c> are
    /// treated as <c>0</c>.
    /// </summary>
    public double ReplaySpeed { get; set; } = 1.0;

    /// <summary>
    /// When <c>true</c>, proactive emitters replay their step sequence
    /// on repeat. Has no effect on request-driven replay paths — those
    /// are handled by the mock server's matcher, not by emitters.
    /// </summary>
    public bool Loop { get; set; }
}
