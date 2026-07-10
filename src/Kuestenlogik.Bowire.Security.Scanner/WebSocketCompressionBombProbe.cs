// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.WebSockets;
using System.Text;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Active WebSocket probe (#397), rolling up to <c>API4:2023 — Unrestricted
/// Resource Consumption</c> via <c>CWE-409 — Improper Handling of Highly
/// Compressed Data (Data Amplification)</c>. Negotiates the
/// <c>permessage-deflate</c> extension and sends a <b>bounded</b>, highly
/// compressible frame (a few MiB of a single repeated character): with
/// compression on the wire this is a tiny compressed frame that decompresses
/// hugely on the server — a "zip-bomb over WebSocket" amplification lever.
///
/// <para><b>Self-contained connection.</b> Unlike the other WebSocket probes,
/// this one does <i>not</i> drive the injected <see cref="IBowireProtocol"/>:
/// negotiating permessage-deflate is a security concern that must not leak into
/// the shared plugin / discovery path. It opens its own
/// <see cref="ClientWebSocket"/> (BCL, no new dependency), sets
/// <see cref="ClientWebSocketOptions.DangerousDeflateOptions"/>, and observes
/// the connection directly.</para>
///
/// <para>Detection, NOT exhaustion — the payload is capped at a few MiB and the
/// observation window is short. Honest about the window: the server closing the
/// connection shortly after the bomb (e.g. 1009 Message Too Big / a protocol
/// error) ⇒ it caps decompressed size/ratio (Safe); the connection surviving
/// the short window ⇒ "no size/ratio limit observed" (Vulnerable). If
/// permessage-deflate isn't negotiated or the send can't complete ⇒
/// inconclusive (Skipped).</para>
///
/// <para>Mutating + aggressive (it forces a large server-side decompression),
/// so <c>--active</c>-gated; the socket is disposed as soon as the verdict is
/// reached.</para>
/// </summary>
internal sealed class WebSocketCompressionBombProbe : IActiveProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API4:2023");

    public string ProtocolId => "websocket";

    // Bounded, highly-compressible payload: a single repeated byte deflates to a
    // tiny frame (DEFLATE tops out near 1032:1 for repeated bytes), so ~4 MiB
    // uncompressed leaves the wire as a few KB but the server must inflate it
    // back to 4 MiB. Detection-sized — big enough to trip a size/ratio cap,
    // small enough to never be an exhaustion attack in its own right.
    private const int PayloadBytes = 4 * 1024 * 1024;
    private const char FillChar = 'A';

    // How long we wait, after sending the bomb, for the server to react. A
    // server that caps decompressed size rejects + closes almost immediately;
    // one that inflates the whole frame stays open. Kept short (bounded by the
    // operator budget) — this is a fast decision, not a soak.
    private const int MaxSettleSeconds = 30;

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(
        string target, IBowireProtocol protocol, IList<string> authHeaders, ActiveScanOptions active, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(active);
        // The injected protocol is intentionally ignored — this probe brings its
        // own permessage-deflate connection (see class remarks).
        if (!IsWebSocket(target) || !Uri.TryCreate(target, UriKind.Absolute, out var uri)) return [];

        var settle = TimeSpan.FromSeconds(Math.Clamp(active.DurationSeconds, 1, MaxSettleSeconds));

        using var socket = new ClientWebSocket();
        socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        ApplyAuthHeaders(socket, authHeaders);

        try
        {
            try
            {
                await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return [Marker(ScanFindingStatus.Skipped, "API4-WS-COMPRESSION-BOMB-INCONCLUSIVE",
                    "WebSocket compression-bomb probe inconclusive",
                    $"Could not open a permessage-deflate WebSocket ({ex.GetType().Name}) — the target may not accept the upgrade / the compression extension, or is unreachable; decompression-limit enforcement not determined.")];
            }

            // Bounded, highly-compressible text frame wrapped in the channel's
            // JSON envelope shape. permessage-deflate compresses the repeated
            // fill to a small frame; the server inflates it back to PayloadBytes.
            var envelope = BuildBombEnvelope();
            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(envelope), WebSocketMessageType.Text, endOfMessage: true, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return [Marker(ScanFindingStatus.Skipped, "API4-WS-COMPRESSION-BOMB-INCONCLUSIVE",
                    "WebSocket compression-bomb probe inconclusive",
                    $"The compressed frame could not be sent ({ex.GetType().Name}) — permessage-deflate may not have been negotiated, or the connection dropped before the frame left; decompression-limit enforcement not determined.")];
            }

            var closedByServer = await ServerClosedWithinAsync(socket, settle, ct).ConfigureAwait(false);
            var ratio = PayloadBytes / 1024; // ~N:1 order-of-magnitude for a single repeated byte over DEFLATE.
            return closedByServer
                ? [Marker(ScanFindingStatus.Safe, "API4-WS-COMPRESSION-BOMB-CAPPED",
                    "WebSocket server caps decompressed frame size",
                    $"The server closed the connection shortly after a {PayloadBytes / (1024 * 1024)} MiB highly-compressible frame — it enforces a decompressed size / ratio limit, so a permessage-deflate compression bomb can't force unbounded inflation.")]
                : [Finding("BWR-OWASP-API4-WS-COMPRESSION-BOMB",
                    "WebSocket server decompresses without a size/ratio limit",
                    $"The server decompressed a permessage-deflate frame that inflates {PayloadBytes / (1024 * 1024)} MiB from a few-KB compressed frame (~{ratio}:1 amplification) and kept the connection open, with no observed size or compression-ratio limit. A client can send tiny compressed frames that force large server-side allocations — a decompression-amplification (zip-bomb) memory-exhaustion lever.",
                    "Cap the maximum decompressed message size and the compression ratio for inbound WebSocket frames (permessage-deflate). Reject / close (1009 Message Too Big) once a frame inflates past the cap, and prefer a conservative server-side window / disable client-context takeover where the traffic doesn't need it.",
                    "medium", 5.3)];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    closeCts.CancelAfter(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe complete", closeCts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception) { /* best-effort close */ }
        }
    }

    // True if the server closes the connection within the window (it rejected /
    // capped the inflated frame); false if the connection survives the window
    // (the server inflated the whole frame with no observed limit).
    private static async Task<bool> ServerClosedWithinAsync(ClientWebSocket socket, TimeSpan budget, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var drain = DrainUntilCloseAsync(socket, cts.Token);
        var winner = await Task.WhenAny(drain, Task.Delay(budget, cts.Token)).ConfigureAwait(false);
        // The read side finishing before the budget ⇒ the server closed / dropped
        // the connection after the bomb. Decide *before* cancelling: cancelling a
        // pending ReceiveAsync aborts the socket, so its post-cancel state can't be
        // trusted — the race winner is the honest signal.
        var closedByServer = winner == drain;
        await cts.CancelAsync().ConfigureAwait(false);
        // Observe the drain task (it faults with OCE once we cancel).
        try { await drain.ConfigureAwait(false); } catch (Exception) { /* close / cancel / error is the signal */ }
        return closedByServer;
    }

    // Reads frames until the server closes (or the socket errors). A normal data
    // frame just continues the loop; only a Close frame / fault ends it — which
    // is exactly the "server rejected the bomb" signal.
    private static async Task DrainUntilCloseAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
        }
    }

    private static string BuildBombEnvelope()
    {
        // Same wrapped shape the WebSocket channel / JS layer produces:
        //   { "type": "text", "text": "AAAA…" }
        var sb = new StringBuilder(PayloadBytes + 32);
        sb.Append("{\"type\":\"text\",\"text\":\"");
        sb.Append(FillChar, PayloadBytes);
        sb.Append("\"}");
        return sb.ToString();
    }

    private static void ApplyAuthHeaders(ClientWebSocket socket, IList<string> authHeaders)
    {
        if (authHeaders is null) return;
        foreach (var raw in authHeaders)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var colon = raw.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var name = raw[..colon].Trim();
            if (name.Length == 0) continue;
            try { socket.Options.SetRequestHeader(name, raw[(colon + 1)..].TrimStart()); }
            catch (ArgumentException) { /* skip a header name the runtime rejects */ }
        }
    }

    private static bool IsWebSocket(string target)
        => Uri.TryCreate(target, UriKind.Absolute, out var u) && (u.Scheme == "ws" || u.Scheme == "wss");

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-409", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the active WebSocket compression-bomb probe."),
        Status = status,
        Detail = detail,
    };
}
