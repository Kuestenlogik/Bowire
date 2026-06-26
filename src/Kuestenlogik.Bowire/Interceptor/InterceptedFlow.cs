// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Interceptor;

/// <summary>
/// One request/response pair observed by
/// <see cref="BowireInterceptorMiddleware"/> as it sat between the host's
/// routing and endpoint stages. Shape mirrors
/// <see cref="Kuestenlogik.Bowire.Proxy.CapturedFlow"/> on purpose — the
/// workbench "Intercepted" rail reuses the same detail-pane renderer as
/// the standalone proxy's "Proxy" rail. Only the trigger differs: this
/// surface is filled by every request the host receives, not by a
/// CONNECT-tunnelled MITM session.
/// </summary>
/// <remarks>
/// <para>
/// The interceptor never modifies the request or response in Phase A —
/// it sees the bytes flowing through the pipeline and records them.
/// Mutation + short-circuit hooks are Phase B / D work and will mount
/// into the same flow shape so the rail surface stays stable across
/// the rollout.
/// </para>
/// <para>
/// Streaming-style endpoints (SSE / chunked / WebSocket upgrade) are
/// detected by the middleware and recorded with empty bodies — the
/// <see cref="Streaming"/> flag tells the UI to label them as such
/// instead of pretending an empty body is an actual empty payload.
/// </para>
/// </remarks>
public sealed class InterceptedFlow
{
    /// <summary>Stable id assigned at capture-time (monotonic per process).</summary>
    public long Id { get; init; }

    /// <summary>Wall-clock timestamp the request entered the middleware.</summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>HTTP verb (GET / POST / …) as the host received it.</summary>
    public string Method { get; init; } = "";

    /// <summary>
    /// Absolute URL the host received the request at — scheme + host +
    /// path + query, reassembled from <c>HttpRequest.Scheme</c> /
    /// <c>Host</c> / <c>Path</c> / <c>QueryString</c>.
    /// </summary>
    public string Url { get; init; } = "";

    /// <summary>Scheme (<c>http</c> or <c>https</c>) the request arrived on.</summary>
    public string Scheme { get; init; } = "http";

    /// <summary>
    /// Path-only view (no scheme / authority) for the sidebar's
    /// space-constrained list rendering. The full <see cref="Url"/>
    /// stays available for the detail pane.
    /// </summary>
    public string Path { get; init; } = "";

    /// <summary>Request headers as captured (case-preserved).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> RequestHeaders { get; init; }
        = Array.Empty<KeyValuePair<string, string>>();

    /// <summary>Request body bytes — kept as UTF-8 text when possible, base64 in <see cref="RequestBodyBase64"/> when binary.</summary>
    public string? RequestBody { get; init; }

    /// <summary>Base64 of the request body when it wasn't safe UTF-8.</summary>
    public string? RequestBodyBase64 { get; init; }

    /// <summary>
    /// Set when the request body exceeded the configured capture cap
    /// (<see cref="BowireInterceptorOptions.MaxBodyBytes"/>). The bytes
    /// up to the cap are still recorded in
    /// <see cref="RequestBody"/> / <see cref="RequestBodyBase64"/>;
    /// this flag tells the UI to surface a "truncated" badge.
    /// </summary>
    public bool RequestBodyTruncated { get; init; }

    /// <summary>HTTP status code the endpoint emitted. 0 when the endpoint threw before writing one.</summary>
    public int ResponseStatus { get; init; }

    /// <summary>Response headers as the host wrote them (case-preserved).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders { get; init; }
        = Array.Empty<KeyValuePair<string, string>>();

    /// <summary>Response body (UTF-8 or null when <see cref="ResponseBodyBase64"/> is set).</summary>
    public string? ResponseBody { get; init; }

    /// <summary>Base64 of the response body when it wasn't safe UTF-8.</summary>
    public string? ResponseBodyBase64 { get; init; }

    /// <summary>Set when the response body exceeded the configured capture cap.</summary>
    public bool ResponseBodyTruncated { get; init; }

    /// <summary>
    /// Set when the middleware detected a streaming response (SSE,
    /// chunked transfer, WebSocket upgrade). The body fields stay
    /// empty — the workbench renders a "streaming" placeholder
    /// instead of an empty payload.
    /// </summary>
    public bool Streaming { get; init; }

    /// <summary>End-to-end latency in milliseconds — wall-clock from middleware entry to endpoint completion.</summary>
    public int LatencyMs { get; init; }

    /// <summary>Error message when the downstream pipeline threw. Null on the happy path.</summary>
    public string? Error { get; init; }
}
