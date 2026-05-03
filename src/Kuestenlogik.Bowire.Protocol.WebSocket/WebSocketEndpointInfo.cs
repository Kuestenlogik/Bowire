// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.WebSocket;

/// <summary>
/// One discovered (or manually registered) WebSocket endpoint.
/// </summary>
public sealed record WebSocketEndpointInfo(
    string Path,
    string? DisplayName = null,
    string? Description = null,
    string? Group = null);
