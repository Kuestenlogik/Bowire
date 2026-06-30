// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Flows.Expectations;

/// <summary>
/// Captured result of one flow-step's request invocation — the input
/// surface every <see cref="FlowExpectation"/> evaluates against. Kept
/// transport-agnostic on purpose: the in-browser runner builds it from
/// fetch/XHR, the CLI runner (T2) builds it from <c>IBowireProtocol</c>
/// invocations, and the evaluator stays the same in both worlds.
/// </summary>
public sealed class FlowRequestEnvelope
{
    /// <summary>
    /// Protocol-equivalent status string. For REST that's the HTTP code
    /// or its name ("200" / "OK"); for gRPC, the trailer ("OK", "NotFound");
    /// for MQTT / WebSocket, the underlying transport state.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>Raw response body verbatim, or null when no body was returned.</summary>
    public string? Body { get; init; }

    /// <summary>Response headers (case-insensitive lookup) as captured.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Wall-clock duration in milliseconds.</summary>
    public long LatencyMs { get; init; }

    /// <summary>Error text when the call failed before assertions could run; null on success.</summary>
    public string? Error { get; init; }
}
