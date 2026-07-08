// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Protocol probe for WebSocket, rolling up to <c>API4:2023 — Unrestricted
/// Resource Consumption</c>. A WebSocket connection is a long-lived, framed
/// byte stream: without an inbound message-size cap a client can push
/// arbitrarily large frames, and the server buffers each one whole before it
/// can act — a memory-amplification / denial-of-service lever (a handful of
/// multi-megabyte frames per connection exhausts server memory). This is the
/// real-time analog of the REST 1 MB body check the HTTP
/// <see cref="Api4ResourceProbe"/> performs.
///
/// <para>Black-box and non-destructive. The probe opens a socket (auth headers
/// ride along so an authenticated endpoint lets it in), sends ONE bounded
/// oversized text frame (<see cref="OversizeBytes"/> bytes ≈ 1 MB), and watches
/// for the server's reaction within a short read window. A server that enforces
/// a limit closes the connection (canonically with <c>1009 Message Too Big</c>)
/// or emits an error — that is Safe. A server that swallows the oversized frame
/// silently and keeps the socket open has no cap (Vulnerable). It is a detection
/// probe, not an exploit: the frame is deliberately bounded — big enough to
/// exceed any sane cap, small enough that it cannot itself harm a target. A
/// non-WebSocket or unreachable endpoint is inconclusive, never a false pass.</para>
/// </summary>
internal sealed class WebSocketResourceLimitProbe : IOwaspProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API4:2023");

    public string ProtocolId => "websocket";

    /// <summary>
    /// Size of the single oversized frame, in bytes (≈ 1 MB). Deliberately
    /// bounded: large enough to exceed any sane inbound message-size cap (most
    /// caps sit well below 1 MB), small enough that a single frame is harmless —
    /// this DETECTS the absence of a cap, it does not try to exhaust the target.
    /// </summary>
    private const int OversizeBytes = 1_000_000;

    /// <summary>
    /// How long to wait for the server to react to the oversized frame. A cap
    /// fires promptly (close / error frame); when the window elapses with the
    /// socket still open and no rejection, the frame was accepted silently.
    /// </summary>
    private const int ReadWindowSeconds = 4;

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct)
    {
        // 1. Open the socket. Auth headers fold into upgrade-request metadata so
        //    an authenticated endpoint admits the probe — the check is about the
        //    size cap, not about auth.
        IBowireChannel? channel;
        try
        {
            channel = await protocol.OpenChannelAsync(target, service: "", method: "",
                showInternalServices: false, metadata: ToMetadata(authHeaders), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker(ScanFindingStatus.Skipped, "API4-WS-INCONCLUSIVE", "WebSocket resource-limit probe inconclusive",
                $"The WebSocket open failed ({ex.GetType().Name}) — the target likely isn't a WebSocket endpoint; message-size limit not determined.")];
        }

        if (channel is null)
        {
            return [Marker(ScanFindingStatus.Skipped, "API4-WS-INCONCLUSIVE", "WebSocket resource-limit probe inconclusive",
                "The WebSocket URL could not be resolved from the target — message-size limit not determined.")];
        }

        // 2. Build the bounded oversized text frame, wrapped in the channel's
        //    send shape ({ "type": "text", "text": "..." }).
        var payload = new string('A', OversizeBytes);
        var frame = JsonSerializer.Serialize(new { type = "text", text = payload });

        // 3. Send it. A channel that refuses the send tells us nothing.
        var sent = await channel.SendAsync(frame, ct).ConfigureAwait(false);
        if (!sent)
        {
            try { await channel.CloseAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort close */ }
            finally { await channel.DisposeAsync().ConfigureAwait(false); }

            return [Marker(ScanFindingStatus.Skipped, "API4-WS-INCONCLUSIVE", "WebSocket resource-limit probe inconclusive",
                "The channel refused to send the oversized frame — message-size limit not determined.")];
        }

        // 4. Watch for the server's reaction inside a bounded read window.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(ReadWindowSeconds));
        try
        {
            await foreach (var env in channel.ReadResponsesAsync(cts.Token).ConfigureAwait(false))
            {
                if (IsCloseOrError(env, out var detail))
                {
                    // Server rejected the oversized frame → cap enforced.
                    return [Marker(ScanFindingStatus.Safe, "API4-WS-MSGCAP-ENFORCED", "WebSocket message-size limit enforced",
                        $"The server rejected a {OversizeBytes:N0}-byte frame ({detail}) — an inbound message-size limit is enforced.")];
                }
            }

            // Stream completed with no close / error → the oversized frame was
            // accepted silently.
            return [NoCapFinding()];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our read window elapsed, the socket is still open, and no close /
            // error arrived → the server never rejected the oversized frame.
            return [NoCapFinding()];
        }
        finally
        {
            try { await channel.CloseAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort close */ }
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True when the envelope is a server <c>close</c> or <c>error</c> frame —
    /// the two shapes the channel emits when the server tears the connection
    /// down or surfaces a receive failure. For a close, the numeric
    /// <c>status</c> (e.g. 1009) is folded into <paramref name="detail"/>.
    /// </summary>
    private static bool IsCloseOrError(string env, out string detail)
    {
        detail = "";
        try
        {
            using var doc = JsonDocument.Parse(env);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var type = typeProp.GetString();
            if (type == "close")
            {
                detail = root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number
                    ? $"closed with {status.GetInt32()}"
                    : "connection closed";
                return true;
            }

            if (type == "error")
            {
                detail = "error frame";
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---- finding factories ----

    private ScanFinding NoCapFinding() => Finding(
        "BWR-OWASP-API4-WS-NOMSGCAP", "WebSocket accepts oversized messages (no size cap)",
        $"A single {OversizeBytes:N0}-byte (≈ 1 MB) text frame was accepted without the server closing the connection or returning an error — the endpoint enforces no inbound message-size limit. Because the server buffers each frame whole before processing it, a few large frames per connection can exhaust server memory: a memory-amplification / denial-of-service lever unique to long-lived socket streams.",
        "Set a maximum receive message / frame size and close oversize frames with 1009 (Message Too Big). ASP.NET Core: cap the per-message buffer when reading (WebSocketAcceptContext / WebSocketOptions.ReceiveBufferSize + your own accumulated-length guard) and abort once a message exceeds the budget. Add a gateway body cap (nginx `client_max_body_size`, or the equivalent proxy frame-size limit) and a per-connection buffering / backpressure limit so a single client cannot pin unbounded memory.",
        "medium", 5.3);

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-400", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the WebSocket message-size probe."),
        Status = status,
        Detail = detail,
    };

    /// <summary>
    /// Fold the scan's <c>--auth-header</c> values (<c>Name: Value</c> strings)
    /// into a metadata dictionary the plugin applies to the upgrade request, so
    /// an authenticated socket admits the probe instead of bouncing it.
    /// </summary>
    private static Dictionary<string, string>? ToMetadata(IList<string> authHeaders)
    {
        if (authHeaders is null || authHeaders.Count == 0) return null;
        var md = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in authHeaders.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            var colon = raw.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var name = raw[..colon].Trim();
            var value = raw[(colon + 1)..].TrimStart();
            if (name.Length > 0) md[name] = value;
        }
        return md.Count > 0 ? md : null;
    }
}
