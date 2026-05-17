// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Proxy;

/// <summary>
/// One captured request/response pair flowing through the
/// <c>bowire proxy</c> intercepting proxy. Records the wire-level
/// shape (method / URL / headers / body) on both sides so the
/// workbench can replay any captured flow as a Bowire recording —
/// the Tier-3 anchor in <c>docs/architecture/security-testing.md</c>.
/// </summary>
public sealed class CapturedFlow
{
    /// <summary>Stable id assigned at capture-time (monotonic per process).</summary>
    public long Id { get; init; }

    /// <summary>Wall-clock timestamp the request landed at the proxy.</summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>HTTP verb (GET / POST / …).</summary>
    public string Method { get; init; } = "";

    /// <summary>Absolute target URL the proxy was asked to forward to.</summary>
    public string Url { get; init; } = "";

    /// <summary>Scheme (<c>http</c> or <c>https</c>) — useful to flag MITM'd flows in the UI.</summary>
    public string Scheme { get; init; } = "http";

    /// <summary>Request headers as captured (case-preserved).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> RequestHeaders { get; init; }
        = Array.Empty<KeyValuePair<string, string>>();

    /// <summary>Request body bytes — kept as UTF-8 text when possible, base64 in <see cref="RequestBodyBase64"/> when binary.</summary>
    public string? RequestBody { get; init; }

    /// <summary>Base64 of the request body when it wasn't safe UTF-8.</summary>
    public string? RequestBodyBase64 { get; init; }

    /// <summary>HTTP status of the upstream response. 0 when the forward failed before a response landed.</summary>
    public int ResponseStatus { get; init; }

    /// <summary>Response headers as captured (case-preserved).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders { get; init; }
        = Array.Empty<KeyValuePair<string, string>>();

    /// <summary>Response body (UTF-8 or null when <see cref="ResponseBodyBase64"/> is set).</summary>
    public string? ResponseBody { get; init; }

    /// <summary>Base64 of the response body when it wasn't safe UTF-8.</summary>
    public string? ResponseBodyBase64 { get; init; }

    /// <summary>Wall-clock latency of the forward round-trip in milliseconds.</summary>
    public int LatencyMs { get; init; }

    /// <summary>Error message when the forward failed (target unreachable, TLS handshake failure, etc.).</summary>
    public string? Error { get; init; }
}
