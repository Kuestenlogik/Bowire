// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Protocol probe for WebSocket, rolling up to <c>API2:2023 — Broken
/// Authentication</c>. When <c>--auth-header</c> asserts the endpoint expects a
/// credential, the probe opens a WebSocket handshake <em>without</em> one. A
/// completed upgrade means the server accepts unauthenticated clients — the
/// classic "auth checked on the REST side, forgotten on the socket" gap.
///
/// <para>Connect-and-close only: it never sends a frame, so it can't drive any
/// application behaviour. A <c>401</c>/<c>403</c> on the upgrade is the healthy
/// case (Safe); a non-WebSocket or unreachable target is inconclusive, not a
/// finding. Silent unless <c>--auth-header</c> is supplied — an intentionally
/// public socket must not be misreported as broken auth.</para>
/// </summary>
internal sealed class WebSocketAuthProbe : IOwaspProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API2:2023");

    public string ProtocolId => "websocket";

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct)
    {
        // Only meaningful when a credential is expected — otherwise a public
        // WebSocket accepting anonymous clients isn't a vulnerability.
        if (authHeaders.Count == 0)
            return [];

        IBowireChannel? channel = null;
        try
        {
            // Empty metadata → no credential on the upgrade request.
            channel = await protocol.OpenChannelAsync(target, service: "", method: "",
                showInternalServices: false, metadata: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var msg = ex.Message ?? "";
            var authRejected = msg.Contains("401", StringComparison.Ordinal) || msg.Contains("403", StringComparison.Ordinal)
                || msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) || msg.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);
            return authRejected
                ? [Marker(ScanFindingStatus.Safe, "API2-WS-AUTH-ENFORCED", "WebSocket auth enforced",
                    $"An anonymous WebSocket upgrade was rejected ({msg.Trim()}) — the endpoint enforces authentication on the handshake.")]
                : [Marker(ScanFindingStatus.Skipped, "API2-WS-INCONCLUSIVE", "WebSocket auth check inconclusive",
                    $"An anonymous WebSocket upgrade failed ({ex.GetType().Name}) — the target likely isn't a WebSocket endpoint; auth enforcement not determined.")];
        }

        if (channel is null)
        {
            return [Marker(ScanFindingStatus.Skipped, "API2-WS-INCONCLUSIVE", "WebSocket auth check inconclusive",
                "The WebSocket URL could not be resolved from the target — auth enforcement not determined.")];
        }

        try { await channel.CloseAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort close */ }
        finally { await channel.DisposeAsync().ConfigureAwait(false); }

        return [Finding("BWR-OWASP-API2-WS-NOAUTH", "WebSocket accepts unauthenticated connections",
            "A WebSocket upgrade completed with no credential (despite --auth-header being supplied) — the endpoint accepts anonymous socket connections. Auth enforced on the HTTP surface is commonly forgotten on the WebSocket upgrade, leaving the real-time channel open to any client.",
            "Authenticate the WebSocket handshake: validate the credential (token / cookie / Sec-WebSocket-Protocol) in the upgrade request and reject unauthenticated upgrades with 401 before switching protocols. Also validate the Origin header to block cross-site socket hijacking.",
            "high", 7.5)];
    }

    // ---- finding factories ----

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-306", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the WebSocket auth probe."),
        Status = status,
        Detail = detail,
    };
}
