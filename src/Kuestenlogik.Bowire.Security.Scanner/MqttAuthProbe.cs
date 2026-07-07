// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Protocol probe for MQTT, rolling up to <c>API2:2023 — Broken
/// Authentication</c>. When <c>--auth-header</c> asserts a credential is
/// expected, the probe issues an anonymous MQTT <c>CONNECT</c> (the plugin
/// connects with no username / password) and watches for a broker that accepts
/// it — the classic "auth bypass on CONNECT", where a broker meant to be
/// private lets any client connect and subscribe.
///
/// <para>Connect + subscribe to a private throwaway topic only — no publish, so
/// it can't inject a message. Runs only when the target is addressed as a broker
/// (<c>mqtt://</c> / <c>mqtts://</c> / <c>tcp://</c> / <c>ssl://</c>) and
/// <c>--auth-header</c> is supplied, so an ordinary HTTP scan never opens a
/// broker socket. A rejected / unreachable CONNECT is inconclusive, not a
/// finding.</para>
/// </summary>
internal sealed class MqttAuthProbe : IOwaspProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API2:2023");

    // Schemes that address an MQTT broker. Guard on these so an HTTP scan
    // target never triggers a broker TCP connect.
    private static readonly string[] s_brokerSchemes = ["mqtt", "mqtts", "tcp", "ssl"];

    public string ProtocolId => "mqtt";

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct)
    {
        // Only meaningful when a credential is expected, and only for a target
        // actually addressed as an MQTT broker.
        if (authHeaders.Count == 0 || !LooksLikeBroker(target))
            return [];

        IBowireChannel? channel = null;
        try
        {
            // Anonymous CONNECT (the plugin wires no credentials) + subscribe to
            // a private throwaway topic. No publish → no message injected.
            channel = await protocol.OpenChannelAsync(target, service: "", method: "bowire/probe/$anon",
                showInternalServices: false, metadata: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker(ScanFindingStatus.Skipped, "API2-MQTT-INCONCLUSIVE", "MQTT auth check inconclusive",
                $"An anonymous MQTT CONNECT could not complete ({ex.GetType().Name}) — broker unreachable or rejected the connection; auth enforcement not determined.")];
        }

        if (channel is null)
        {
            return [Marker(ScanFindingStatus.Skipped, "API2-MQTT-INCONCLUSIVE", "MQTT auth check inconclusive",
                "The broker refused the anonymous CONNECT (or was unreachable) — this may be enforced auth or an unreachable target; not determined.")];
        }

        try { await channel.CloseAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort close */ }
        finally { await channel.DisposeAsync().ConfigureAwait(false); }

        return [Finding("BWR-OWASP-API2-MQTT-NOAUTH", "MQTT broker accepts anonymous CONNECT",
            "The broker accepted an MQTT CONNECT with no username / password (despite --auth-header being supplied) and allowed a subscribe. An anonymous-connect broker lets any client subscribe to (and publish on) topics — a direct path to eavesdropping on device telemetry and injecting commands.",
            "Disable anonymous access on the broker and require per-client credentials (username/password or mTLS client certs). Mosquitto: `allow_anonymous false` + a password file / auth plugin. Enforce a topic ACL so authenticated clients only reach their own topics.",
            "high", 7.5)];
    }

    private static bool LooksLikeBroker(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        var i = target.IndexOf("://", StringComparison.Ordinal);
        if (i <= 0) return false;
        var scheme = target[..i];
        return s_brokerSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase);
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
            remediation: "Diagnostic marker for the MQTT auth probe."),
        Status = status,
        Detail = detail,
    };
}
