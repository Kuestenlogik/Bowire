// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Active WebSocket probe (#398), rolling up to <c>API4:2023 — Unrestricted
/// Resource Consumption</c> (CWE-400). Opens a WebSocket and holds it idle for
/// up to <c>--active-duration</c>, watching whether the server enforces an
/// idle / slow-read timeout or lets a client pin the connection open
/// indefinitely (the slow-loris resource-exhaustion lever).
///
/// <para>Honest about the window: the server closing the idle connection within
/// the budget ⇒ it enforces a timeout (Safe, reported at ~when it closed); the
/// connection surviving the full budget ⇒ "no idle timeout observed within Ns"
/// (the finding names N). The seam sends whole frames, not partial byte drips,
/// so this exercises the idle/read-timeout defence rather than half-open frame
/// starvation.</para>
///
/// <para>Aggressive + slow (holds a connection open), so <c>--active</c>-gated;
/// the socket is closed as soon as the verdict is reached.</para>
/// </summary>
internal sealed class WebSocketSlowLorisProbe : IActiveProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API4:2023");

    public string ProtocolId => "websocket";

    private const int MaxHoldSeconds = 120;

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(
        string target, IBowireProtocol protocol, IList<string> authHeaders, ActiveScanOptions active, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(active);
        if (!IsWebSocket(target)) return [];

        var seconds = Math.Clamp(active.DurationSeconds, 1, MaxHoldSeconds);
        IBowireChannel? channel;
        try
        {
            channel = await protocol.OpenChannelAsync(target, service: "WebSocket", method: "/", showInternalServices: false, metadata: null, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker(ScanFindingStatus.Skipped, "API4-WS-SLOWLORIS-INCONCLUSIVE",
                "WebSocket slow-loris probe inconclusive",
                $"Could not open a WebSocket ({ex.GetType().Name}) — the target may not accept the upgrade or is unreachable; idle-timeout enforcement not determined.")];
        }
        if (channel is null)
        {
            return [Marker(ScanFindingStatus.Skipped, "API4-WS-SLOWLORIS-INCONCLUSIVE",
                "WebSocket slow-loris probe inconclusive",
                "The server refused the WebSocket upgrade (or was unreachable) — idle-timeout enforcement not determined.")];
        }

        try
        {
            var closedByServer = await ServerClosedWithinAsync(channel, TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
            return closedByServer
                ? [Marker(ScanFindingStatus.Safe, "API4-WS-SLOWLORIS-TIMEOUT",
                    "WebSocket server enforces an idle timeout",
                    $"The server closed an idle WebSocket within {seconds}s — a slow-loris client can't pin the connection open indefinitely.")]
                : [Finding("BWR-OWASP-API4-WS-SLOWLORIS", $"No WebSocket idle timeout observed within {seconds}s",
                    $"An idle WebSocket stayed open for the full {seconds}s budget with no server-side idle/read timeout. A slow-loris client can hold connections open indefinitely and exhaust the server's connection/socket budget.",
                    $"Enforce an idle / read timeout on WebSocket connections (server keep-alive + idle-close, or a proxy idle timeout) and cap concurrent connections per client. Re-run with a longer --active-duration to probe a larger window.",
                    "medium", 5.3)];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            try { await channel.CloseAsync(ct).ConfigureAwait(false); } catch (Exception) { /* best-effort */ }
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    // True if the server closes the idle connection within the budget; false if
    // it survives the whole window.
    private static async Task<bool> ServerClosedWithinAsync(IBowireChannel channel, TimeSpan budget, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var drain = DrainUntilCloseAsync(channel, cts.Token);
        var winner = await Task.WhenAny(drain, Task.Delay(budget, cts.Token)).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        // Always observe the drain task (it faults with OCE once we cancel).
        try { await drain.ConfigureAwait(false); } catch (Exception) { /* close / cancel / error is the signal */ }
        // Read side ended before the budget ⇒ the server closed the connection.
        return winner == drain || channel.IsClosed;
    }

    private static async Task DrainUntilCloseAsync(IBowireChannel channel, CancellationToken ct)
    {
        // Enumerating the read side blocks on the server; it only ends when the
        // server closes (or errors) the connection — which is exactly the signal.
        await foreach (var _ in channel.ReadResponsesAsync(ct).ConfigureAwait(false)) { }
    }

    private static bool IsWebSocket(string target)
        => Uri.TryCreate(target, UriKind.Absolute, out var u) && (u.Scheme == "ws" || u.Scheme == "wss");

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-400", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the active WebSocket slow-loris probe."),
        Status = status,
        Detail = detail,
    };
}
