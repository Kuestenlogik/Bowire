// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Protocol probe for MQTT, rolling up to <c>API8:2023 — Security
/// Misconfiguration</c>. Reports credentials and control traffic crossing an
/// MQTT link in the clear.
///
/// <para>The sibling <see cref="MqttAuthProbe"/> asks whether the broker lets
/// anyone in. This one asks the opposite-shaped question: a broker that
/// correctly <em>demands</em> credentials, and then takes them over plaintext,
/// passes that probe cleanly while leaking every one of them. MQTT's CONNECT
/// carries the username and password as plain fields in the packet — there is
/// no challenge-response to hide behind — so an unencrypted link hands them to
/// anyone on the path.</para>
///
/// <para>Deliberately a deployment finding, not a defect one. Plenty of brokers
/// sit behind a TLS-terminating proxy or on a segment where plaintext is a
/// considered choice, so the wording says what was observed — credentials
/// crossed this link in the clear — rather than declaring the broker broken.
/// It runs only when <c>--auth-header</c> asserts a credential is expected,
/// for the same reason <see cref="MqttAuthProbe"/> does: without one there is
/// nothing to be exposed.</para>
///
/// <para>Raised from Kuestenlogik/Bowire.VulnDb#25, where the CVE is a
/// deployment misconfiguration rather than a product defect — which is exactly
/// what a scanner can see and a version check cannot.</para>
/// </summary>
internal sealed class MqttCleartextProbe : IOwaspProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API8:2023");

    public string ProtocolId => "mqtt";

    // Schemes that address an MQTT broker without transport security. `tcp` is
    // the scheme most client libraries use for plain MQTT, and `mqtt` is the
    // registered one; both mean the same wire.
    private static readonly string[] s_plaintextSchemes = ["mqtt", "tcp"];

    // The encrypted counterparts, listed so the probe can say "this broker does
    // offer TLS, and this connection did not use it" — a different remediation
    // from "there is no TLS listener at all".
    private static readonly string[] s_secureSchemes = ["mqtts", "ssl", "tls"];

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(
        string target, IBowireProtocol protocol, IList<string> authHeaders, CancellationToken ct)
    {
        // No credential expected → nothing to expose. Same gate as the auth
        // probe: a public telemetry broker on plaintext is a design decision,
        // not a finding.
        if (authHeaders.Count == 0) return [];

        var scheme = SchemeOf(target);
        if (scheme is null) return [];

        if (s_secureSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase))
        {
            // Addressed over TLS. Whether the certificate is any good is
            // SslProbeExecutor's question, not this one.
            return [];
        }

        if (!s_plaintextSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase)) return [];

        // Confirm the broker actually talks on the plaintext endpoint before
        // reporting. A scheme in a URL is an intention; a completed CONNECT is
        // an observation, and only the second is worth a finding.
        IBowireChannel? channel = null;
        try
        {
            channel = await protocol.OpenChannelAsync(
                target, service: "", method: "bowire/probe/$cleartext",
                showInternalServices: false, metadata: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker("API8-MQTT-CLEARTEXT-INCONCLUSIVE", "MQTT cleartext check inconclusive",
                $"The plaintext endpoint could not be reached ({ex.GetType().Name}) — the broker may only listen on TLS, or be unreachable from here. Not determined.")];
        }

        if (channel is null)
        {
            return [Marker("API8-MQTT-CLEARTEXT-INCONCLUSIVE", "MQTT cleartext check inconclusive",
                "No channel came back from the plaintext endpoint — the broker may only listen on TLS, or be unreachable from here. Not determined.")];
        }

        try { await channel.CloseAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort close */ }
        finally { await channel.DisposeAsync().ConfigureAwait(false); }

        return [Finding(
            "BWR-OWASP-API8-MQTT-CLEARTEXT",
            "MQTT credentials cross the link in the clear",
            $"The broker was reached over `{scheme}://`, which carries no transport encryption, while --auth-header asserts that a credential is expected. "
            + "MQTT sends the username and password as plain fields inside the CONNECT packet, so anyone on the network path — a shared segment, a transit provider, a compromised switch — reads them as they go by, along with every topic name and payload that follows. "
            + "Captured credentials let an attacker impersonate the device rather than merely observe it.",
            "Address the broker over `mqtts://` (8883) and require TLS on the listener, so a plaintext connection is refused rather than merely discouraged. "
            + "Mosquitto: a `listener 8883` with `certfile` / `keyfile`, and no plaintext listener bound to a routable interface. "
            + "Where the broker sits behind a TLS-terminating proxy, keep the plaintext listener on loopback or a private segment so it cannot be addressed the way this scan addressed it. "
            + "Rotate any credential that has been used over the plaintext endpoint — it should be treated as disclosed.",
            "medium", 5.9)];
    }

    private static string? SchemeOf(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var i = target.IndexOf("://", StringComparison.Ordinal);
        return i <= 0 ? null : target[..i];
    }

    // ---- finding factories ----

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        // CWE-319: Cleartext Transmission of Sensitive Information.
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-319", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the MQTT cleartext probe."),
        Status = ScanFindingStatus.Skipped,
        Detail = detail,
    };
}
