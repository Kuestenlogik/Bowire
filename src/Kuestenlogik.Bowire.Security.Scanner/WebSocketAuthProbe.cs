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
/// <para>When the anonymous upgrade is refused and the credential is a JWT, the
/// probe opens the upgrade twice more with forged tokens, the same two
/// <see cref="Api2AuthProbe"/> sends over HTTP: the token re-headed to
/// <c>alg:none</c>, and the token with a corrupted signature. A completed
/// upgrade with either means the socket checks that a token is present, not
/// that it is genuine — anyone can mint one with any claims
/// (Bowire.VulnDb#51: WebSocket routes that decode the bearer JWT without
/// verifying it, while the HTTP routes of the same server verify it).</para>
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

        var anonymous = await WebSocketHandshake.TryAsync(target, protocol, authHeaders: null, ct).ConfigureAwait(false);
        if (anonymous.Connected)
        {
            return [Finding("BWR-OWASP-API2-WS-NOAUTH", "WebSocket accepts unauthenticated connections",
                "A WebSocket upgrade completed with no credential (despite --auth-header being supplied) — the endpoint accepts anonymous socket connections. Auth enforced on the HTTP surface is commonly forgotten on the WebSocket upgrade, leaving the real-time channel open to any client.",
                "Authenticate the WebSocket handshake: validate the credential (token / cookie / Sec-WebSocket-Protocol) in the upgrade request and reject unauthenticated upgrades with 401 before switching protocols. Also validate the Origin header to block cross-site socket hijacking.",
                "high", 7.5, cwe: "CWE-306")];
        }

        if (!anonymous.AuthRejected)
        {
            var detail = anonymous.Exception is { } ex
                ? $"An anonymous WebSocket upgrade failed ({ex.GetType().Name}) — the target likely isn't a WebSocket endpoint; auth enforcement not determined."
                : "The WebSocket URL could not be resolved from the target — auth enforcement not determined.";
            return [Marker(ScanFindingStatus.Skipped, "API2-WS-INCONCLUSIVE", "WebSocket auth check inconclusive", detail)];
        }

        // Anonymous is refused. Does the socket verify the token, or only
        // look for one? Only a JWT can be forged; an opaque token cannot.
        if (JwtForgery.FindBearer(authHeaders) is not { } bearer || !JwtForgery.LooksLikeJwt(bearer.Token))
        {
            return [Marker(ScanFindingStatus.Safe, "API2-WS-AUTH-ENFORCED", "WebSocket auth enforced",
                $"An anonymous WebSocket upgrade was rejected ({anonymous.Reason}) — the endpoint enforces authentication on the handshake. The credential is not a JWT, so no forged-token variants were tried.")];
        }

        var findings = new List<ScanFinding>();

        var none = await WebSocketHandshake.TryAsync(target, protocol,
            JwtForgery.WithBearer(authHeaders, bearer.HeaderName, JwtForgery.ForgeNone(bearer.Token)), ct).ConfigureAwait(false);
        if (none.Connected)
        {
            findings.Add(Finding("BWR-OWASP-API2-WS-ALG-NONE", "Unsigned JWT (alg:none) accepted on the WebSocket upgrade",
                $"A token re-headed to alg:none completed the WebSocket upgrade while an anonymous upgrade is refused ({anonymous.Reason}). The socket accepts unsigned tokens — anyone can forge any identity, and reach the channel as them.",
                "Reject alg:none on the upgrade as on every HTTP route. Pin the expected algorithm(s) and verify the signature against the issuer key before switching protocols — decoding the token is not verifying it.",
                "critical", 9.8, cwe: "CWE-347"));
        }

        var tampered = await WebSocketHandshake.TryAsync(target, protocol,
            JwtForgery.WithBearer(authHeaders, bearer.HeaderName, JwtForgery.TamperSignature(bearer.Token)), ct).ConfigureAwait(false);
        if (tampered.Connected)
        {
            findings.Add(Finding("BWR-OWASP-API2-WS-SIG", "JWT signature not verified on the WebSocket upgrade",
                $"A token with a corrupted signature completed the WebSocket upgrade while an anonymous upgrade is refused ({anonymous.Reason}). The socket isn't verifying the signature — it checks that a token is present, not that it is genuine.",
                "Verify the JWT signature against the issuer's key in the upgrade request, with the same validation the HTTP routes use, and reject the upgrade with 401 when it fails.",
                "critical", 9.1, cwe: "CWE-347"));
        }

        if (findings.Count == 0)
        {
            findings.Add(Marker(ScanFindingStatus.Safe, "API2-WS-AUTH-ENFORCED", "WebSocket auth enforced",
                $"An anonymous WebSocket upgrade was rejected ({anonymous.Reason}), and so were upgrades with the token re-headed to alg:none ({none.Reason}) and with a corrupted signature ({tampered.Reason}) — the endpoint verifies the token on the handshake."));
        }
        return findings;
    }

    // ---- finding factories ----

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss, string cwe) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: cwe, owaspApi: Entry.Tag, severity, cvss, remediation),
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
