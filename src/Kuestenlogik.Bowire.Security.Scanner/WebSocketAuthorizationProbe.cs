// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Protocol probe for WebSocket, rolling up to <c>API5:2023 — Broken Function
/// Level Authorization</c>. Detects a socket that <em>authenticates</em> the
/// caller and never <em>authorizes</em> them.
///
/// <para>The sibling <see cref="WebSocketAuthProbe"/> asks whether anyone at
/// all may connect. This one asks the question that survives a yes to
/// authentication: a handler that establishes who you are via a session check
/// and then proceeds without consulting the permission model lets any
/// authenticated account reach any channel. Bowire.VulnDb#23 is the shape —
/// in-app terminals where any member could open a shell into any container.</para>
///
/// <para>The verdict needs three observations, the same way
/// <see cref="Api1BolaProbe"/> reaches one: anonymous must be refused (or the
/// endpoint is simply public, which is the other probe's finding), identity A
/// must be accepted (or the endpoint is not one A has any business on), and
/// identity B must then also be accepted. Only that combination shows a socket
/// that gates on <em>having</em> a credential rather than on <em>whose</em> it
/// is.</para>
/// </summary>
internal sealed class WebSocketAuthorizationProbe : IOwaspProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API5:2023");

    public string ProtocolId => "websocket";

    /// <summary>
    /// Without a second identity there is nothing to compare, so the
    /// single-identity entry point stays silent rather than guessing.
    /// </summary>
    public Task<IReadOnlyList<ScanFinding>> RunAsync(
        string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScanFinding>>([]);

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(OwaspProbeContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = context.Target;
        var protocol = context.Protocol;
        var authHeaders = context.AuthHeaders;
        var authHeadersB = context.AuthHeadersB;

        if (authHeaders is null || authHeaders.Count == 0
            || authHeadersB is null || authHeadersB.Count == 0)
        {
            // Not a gap in coverage — a cross-identity check without a second
            // identity has nothing to say, and saying it anyway would be noise
            // on every ordinary scan.
            return [];
        }

        if (SameIdentity(authHeaders, authHeadersB))
        {
            return [Marker(ScanFindingStatus.Skipped, "API5-WS-SAME-IDENTITY",
                "WebSocket authorization check skipped",
                "--auth-header and --auth-header-b carry the same credential, so B reaching what A reaches proves nothing. Supply a second, lower-privileged identity to run this check.")];
        }

        // 1. Anonymous must be refused. If it is not, the socket is open to
        //    everyone and WebSocketAuthProbe reports that — this probe staying
        //    quiet avoids two findings for one hole, and a "B got in" claim
        //    that would be true of any caller at all.
        var anonymous = await TryHandshakeAsync(target, protocol, metadata: null, ct).ConfigureAwait(false);
        if (anonymous.Connected)
        {
            return [Marker(ScanFindingStatus.Skipped, "API5-WS-PUBLIC",
                "WebSocket authorization check not applicable",
                "The endpoint accepted an anonymous upgrade, so it does not gate on identity at all. That is reported by the WebSocket authentication check; a cross-identity comparison adds nothing here.")];
        }

        // 2. Identity A must be accepted, or the comparison has no baseline —
        //    B being refused would say nothing about authorization if A is
        //    refused too.
        var asA = await TryHandshakeAsync(target, protocol, ToMetadata(authHeaders), ct).ConfigureAwait(false);
        if (!asA.Connected)
        {
            return [Marker(ScanFindingStatus.Skipped, "API5-WS-NO-BASELINE",
                "WebSocket authorization check inconclusive",
                $"Identity A could not open the socket either ({asA.Reason}) — with no accepted baseline there is nothing to compare B against. The endpoint may not be a WebSocket, or A may not be entitled to it.")];
        }

        // 3. B in the same place. Accepted means the handler checked that a
        //    credential exists and never checked whose it was.
        var asB = await TryHandshakeAsync(target, protocol, ToMetadata(authHeadersB), ct).ConfigureAwait(false);
        if (!asB.Connected)
        {
            return [Marker(ScanFindingStatus.Safe, "API5-WS-AUTHZ-ENFORCED",
                "WebSocket authorization enforced",
                $"Identity A opened the socket and identity B was refused ({asB.Reason}) — the endpoint distinguishes between authenticated callers rather than merely requiring one.")];
        }

        return [Finding("BWR-OWASP-API5-WS-NOAUTHZ",
            "WebSocket authenticates the caller but does not authorize them",
            "An anonymous upgrade was refused, and upgrades carrying two different identities both succeeded. "
            + "The handler therefore checks that a credential is present and not what it entitles the holder to — every authenticated account reaches this channel, whatever the permission model says elsewhere. "
            + "This is the failure that survives every \"is it public?\" check: the socket looks protected, and is, against strangers only.",
            "Authorize the WebSocket upgrade, not just authenticate it: after establishing who the caller is, consult the same role or permission model the rest of the application enforces, and reject the upgrade when the caller is not entitled to that channel. "
            + "Where the channel is scoped to a resource — a container, a session, a tenant — check that the caller owns or may access that resource specifically, since the identifier usually travels in the path or the query and is attacker-chosen.",
            "high", 8.1)];
    }

    // ---- handshake ----

    private readonly record struct Attempt(bool Connected, string Reason);

    private static async Task<Attempt> TryHandshakeAsync(
        string target, IBowireProtocol protocol, Dictionary<string, string>? metadata, CancellationToken ct)
    {
        IBowireChannel? channel = null;
        try
        {
            channel = await protocol.OpenChannelAsync(target, service: "", method: "",
                showInternalServices: false, metadata: metadata, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Attempt(false, (ex.Message ?? ex.GetType().Name).Trim());
        }

        if (channel is null) return new Attempt(false, "no channel was returned");

        // Connect and close. The probe never sends a frame, so it cannot drive
        // any application behaviour on a channel it may not be entitled to —
        // which matters more here than in the auth probe, because this one
        // deliberately connects as somebody.
        try { await channel.CloseAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort close */ }
        finally { await channel.DisposeAsync().ConfigureAwait(false); }

        return new Attempt(true, "connected");
    }

    private static bool SameIdentity(IList<string> a, IList<string> b)
        => a.Count == b.Count
            && a.OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(b.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal);

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

    // ---- finding factories ----

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        // CWE-285: Improper Authorization — the caller is known, the decision
        // about what they may do is missing.
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-285", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the WebSocket authorization probe."),
        Status = status,
        Detail = detail,
    };
}
