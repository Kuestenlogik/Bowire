// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Active MQTT probe (#395), rolling up to <c>API8:2023 — Security
/// Misconfiguration</c>. Publishes a <b>retained</b> message to a namespaced
/// throwaway topic, then opens a <i>fresh</i> subscription and checks whether
/// the broker persisted + re-delivered it — the retained-message-poisoning
/// vector, where any client that can write a retained message plants
/// persistent state later subscribers pick up on a shared topic.
///
/// <para><b>Mutating.</b> This publishes to the broker, so it runs only under
/// <c>--active</c>. The side effect is bounded + reversible: a unique
/// <c>bowire/probe/&lt;nonce&gt;</c> topic, cleared afterwards by publishing an
/// empty retained payload (the MQTT idiom for deleting a retained message).</para>
///
/// <para>Verdict: retained message re-delivered to a new subscriber ⇒ the
/// broker accepts retained writes on an arbitrary topic (Vulnerable);
/// publish succeeded but nothing was re-delivered ⇒ retained write rejected /
/// not persisted (Safe); publish couldn't complete ⇒ inconclusive (Skipped).</para>
/// </summary>
internal sealed class MqttRetainedPoisoningProbe : IActiveProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API8:2023");

    public string ProtocolId => "mqtt";

    // Schemes that address an MQTT broker (mirrors MqttAuthProbe) — guard so an
    // HTTP scan target never opens a broker socket.
    private static readonly string[] s_brokerSchemes = ["mqtt", "mqtts", "tcp", "ssl"];

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(
        string target, IBowireProtocol protocol, IList<string> authHeaders, ActiveScanOptions active, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(active);
        if (!LooksLikeBroker(target)) return [];

        // Unique, namespaced throwaway topic + marker so a fresh subscriber can
        // recognise our own retained message (crypto RNG — no Random, per CA5394).
        var nonce = RandomNumberGenerator.GetHexString(16, lowercase: true);
        var topic = $"bowire/probe/{nonce}";
        var marker = $"bowire-retained-{nonce}";
        var payload = $"{{\"bowire-probe\":\"{marker}\"}}";
        var retained = new Dictionary<string, string>(StringComparer.Ordinal) { ["retain"] = "true" };

        try
        {
            // 1. Publish the retained message.
            var publish = await protocol.InvokeAsync(
                target, service: "", method: topic, [payload], showInternalServices: false, retained, ct)
                .ConfigureAwait(false);
            if (!string.Equals(publish.Status, "OK", StringComparison.OrdinalIgnoreCase))
            {
                return [Marker(ScanFindingStatus.Skipped, "API8-MQTT-RETAINED-INCONCLUSIVE",
                    "MQTT retained-message probe inconclusive",
                    $"The retained PUBLISH did not complete (status '{publish.Status}') — the broker refused the write or was unreachable; retained-poisoning not determined.")];
            }

            // 2. Fresh subscription — a retained message is delivered to a new
            //    subscriber immediately on SUBSCRIBE, so a short bounded window
            //    is enough (this is not a soak probe).
            var delivered = await FreshSubscriberSawMarkerAsync(protocol, target, topic, marker, ct).ConfigureAwait(false);

            return delivered
                ? [Finding("BWR-OWASP-API8-MQTT-RETAINED-POISONING", "MQTT broker persists arbitrary retained messages",
                    $"A retained message published to '{topic}' was persisted by the broker and re-delivered to a new subscriber. Any client that can write a retained message can plant persistent state that later subscribers on a shared topic pick up — a trust-boundary / message-poisoning vector (stale commands, spoofed telemetry).",
                    "Restrict retained-message publishing with a topic ACL so only trusted producers can set retained state, or disable retained messages where they aren't needed. Mosquitto: `retain_available false`, or per-client ACLs that deny the retain flag on shared topics.",
                    "medium", 5.9)]
                : [Marker(ScanFindingStatus.Safe, "API8-MQTT-RETAINED-CLEAN",
                    "Retained-message write not persisted",
                    "The retained PUBLISH was accepted but a fresh subscriber did not receive it back — the broker did not persist/re-deliver the retained write (retained disabled or ACL-filtered).")];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker(ScanFindingStatus.Skipped, "API8-MQTT-RETAINED-INCONCLUSIVE",
                "MQTT retained-message probe inconclusive",
                $"The retained-message probe could not complete ({ex.GetType().Name}) — broker unreachable or rejected the operation.")];
        }
        finally
        {
            // 3. Cleanup — clear the retained message (empty retained payload is
            //    the MQTT idiom for deleting retained state). Best-effort.
            await ClearRetainedAsync(protocol, target, topic).ConfigureAwait(false);
        }
    }

    private static async Task<bool> FreshSubscriberSawMarkerAsync(
        IBowireProtocol protocol, string target, string topic, string marker, CancellationToken ct)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await foreach (var frame in protocol.InvokeStreamAsync(
                target, service: "", method: topic, [], showInternalServices: false, metadata: null, window.Token)
                .ConfigureAwait(false))
            {
                if (frame.Contains(marker, StringComparison.Ordinal)) return true;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Observation window elapsed with no matching frame → not delivered.
        }
        return false;
    }

    private static async Task ClearRetainedAsync(IBowireProtocol protocol, string target, string topic)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var clear = new Dictionary<string, string>(StringComparer.Ordinal) { ["retain"] = "true" };
            await protocol.InvokeAsync(target, service: "", method: topic, [""], showInternalServices: false, clear, cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort cleanup — a leftover empty retained payload on a
            // throwaway nonce topic is harmless.
        }
    }

    private static bool LooksLikeBroker(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        var i = target.IndexOf("://", StringComparison.Ordinal);
        if (i <= 0) return false;
        return s_brokerSchemes.Contains(target[..i], StringComparer.OrdinalIgnoreCase);
    }

    private ScanFinding Finding(string id, string name, string detail, string remediation, string severity, double cvss) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-501", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the active MQTT retained-message probe."),
        Status = status,
        Detail = detail,
    };
}
