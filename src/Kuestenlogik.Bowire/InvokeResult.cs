// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire;

#pragma warning disable CA1819 // ResponseBinary is raw wire bytes — defensive
                              // copying here would silently double memory
                              // use on every invocation.

/// <summary>
/// Result of a protocol invocation (unary or client-streaming).
/// </summary>
/// <param name="Response">Response as JSON string for display + recording.</param>
/// <param name="DurationMs">Wall-clock duration the invocation took, server-side.</param>
/// <param name="Status">Status label (HTTP code name, gRPC status name, or <c>"OK"</c>).</param>
/// <param name="Metadata">Response headers + trailers (for gRPC, trailer keys are prefixed with <c>_trailer:</c>).</param>
/// <param name="ResponseBinary">
/// Optional raw wire-bytes of the response. Populated by protocols that have
/// a binary serialization distinct from their JSON representation (gRPC
/// protobuf today). Consumed by the mock-server replay path so the recorded
/// response can be re-emitted byte-for-byte without runtime re-encoding.
/// <c>null</c> for protocols where the JSON in <see cref="Response"/> is the
/// authoritative wire form (REST, GraphQL, SignalR).
/// </param>
public sealed record InvokeResult(
    string? Response,
    long DurationMs,
    string Status,
    Dictionary<string, string> Metadata,
    byte[]? ResponseBinary = null);

#pragma warning restore CA1819
