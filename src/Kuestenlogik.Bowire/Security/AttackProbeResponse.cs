// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security;

/// <summary>
/// Captured response context that <see cref="AttackPredicateEvaluator"/>
/// walks an <see cref="AttackPredicate"/> against. Transport-agnostic:
/// the HTTP scanner populates <see cref="Status"/> from the HTTP status
/// code; future gRPC / SignalR / WebSocket transports map their own
/// status concepts onto the same integer (gRPC: 0..16 status codes,
/// SignalR: 200 for invocation OK / specific code on hub error, …).
/// </summary>
/// <remarks>
/// <see cref="Body"/> is the UTF-8-decoded response payload. Binary
/// transports (gRPC native, WebSocket binary frames) base64-encode
/// their body into the same field so the predicate operators
/// (<c>bodyContains</c>, <c>bodyMatches</c>) work uniformly.
/// </remarks>
public sealed class AttackProbeResponse
{
    /// <summary>Status code — HTTP status for HTTP-class transports, gRPC status for gRPC, etc.</summary>
    public int Status { get; init; }

    /// <summary>Headers (response metadata) — keyed case-insensitively at evaluation time.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Response body as UTF-8 text. Binary bodies are base64-encoded into the same field.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Wall-clock round-trip latency, measured from probe send to response receipt.</summary>
    public int LatencyMs { get; init; }

    /// <summary>
    /// Out-of-band callbacks attributed to this probe (#35 Phase 2f). A second
    /// evaluation axis: unlike every field above, these do not come from the
    /// response — they are collected from an interaction server *after* the
    /// probe, and are the only evidence a blind finding has. Empty unless the
    /// operator opted into an interaction server, so a predicate asserting on
    /// them simply never matches when OAST is off.
    /// </summary>
    public IReadOnlyList<ProbeInteraction> Interactions { get; init; } = [];
}
