// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.Sse;

/// <summary>
/// Represents a parsed SSE event for JSON serialization.
/// </summary>
internal sealed record SseEventPayload(
    string? Id,
    string? Event,
    string Data,
    int? Retry);
