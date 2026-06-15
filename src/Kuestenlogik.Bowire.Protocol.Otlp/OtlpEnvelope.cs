// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.Otlp;

/// <summary>
/// A single OTLP export received by the passive listener.
/// </summary>
/// <remarks>
/// Phase 1 keeps the payload as either decoded JSON (when the exporter set
/// <c>Content-Type: application/json</c>) or raw bytes captured as base64
/// (every other content type — the OTLP default is
/// <c>application/x-protobuf</c>). Phase 2 swaps the base64 branch for an
/// inline protobuf decode via vendored opentelemetry-proto descriptors.
/// </remarks>
/// <param name="Kind">Which OTLP signal the envelope carries.</param>
/// <param name="ReceivedAt">UTC instant the receiver enqueued the envelope.</param>
/// <param name="ContentType">Raw <c>Content-Type</c> header from the export.</param>
/// <param name="BodyJson">JSON payload when the exporter encoded as JSON. Null otherwise.</param>
/// <param name="BodyBase64">Base64 bytes when the exporter encoded as protobuf (or anything else). Null when <see cref="BodyJson"/> is set.</param>
/// <param name="BodyBytes">Total payload size in bytes — useful for "how big was the export" before any decode.</param>
/// <param name="RemoteIp">Remote IP of the exporter, when the receiver could resolve it.</param>
public sealed record OtlpEnvelope(
    OtlpSignalKind Kind,
    DateTimeOffset ReceivedAt,
    string ContentType,
    string? BodyJson,
    string? BodyBase64,
    long BodyBytes,
    string? RemoteIp);

/// <summary>
/// OpenTelemetry signal taxonomy — one entry per OTLP endpoint
/// (<c>/v1/traces</c>, <c>/v1/metrics</c>, <c>/v1/logs</c>).
/// </summary>
public enum OtlpSignalKind
{
    Traces,
    Metrics,
    Logs,
}
