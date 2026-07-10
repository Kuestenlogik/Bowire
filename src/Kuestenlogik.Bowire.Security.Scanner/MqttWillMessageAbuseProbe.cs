// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Active MQTT probe (#395, second check), rolling up to <c>API8:2023 —
/// Security Misconfiguration</c>. Connects with a malicious Last-Will-and-
/// Testament pointed at a namespaced throwaway topic, then triggers the will
/// and checks whether the broker delivers it, unfiltered, to a subscriber — the
/// will-message-abuse injection vector (a connecting client can plant a message
/// that lands on others when its session drops).
///
/// <para><b>Rail-isolated.</b> Setting a will on CONNECT and dropping the
/// session to fire it are capabilities the shared <c>BowireMqttProtocol</c>
/// deliberately does NOT expose (that would push security-only complexity into
/// the plugin's discovery / invoke path). So this probe brings its OWN MQTTnet
/// client — it ignores the injected <see cref="IBowireProtocol"/> and leaves the
/// shared plugin untouched. It uses an MQTT-5 "disconnect with will message"
/// to fire the will deterministically; a broker that speaks only 3.1.1 (or
/// rejects the connect) reports inconclusive.</para>
///
/// <para>Mutating (plants a will message), so <c>--active</c>-gated. The will
/// isn't retained, so nothing persists after the probe.</para>
/// </summary>
internal sealed class MqttWillMessageAbuseProbe : IActiveProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API8:2023");

    public string ProtocolId => "mqtt";

    private static readonly string[] s_brokerSchemes = ["mqtt", "mqtts", "tcp", "ssl"];

    // The injected protocol is intentionally unused — see the class remarks.
    public async Task<IReadOnlyList<ScanFinding>> RunAsync(
        string target, IBowireProtocol protocol, IList<string> authHeaders, ActiveScanOptions active, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(active);
        if (!TryParseBroker(target, out var host, out var port)) return [];

        var nonce = RandomNumberGenerator.GetHexString(16, lowercase: true);
        var willTopic = $"bowire/probe/will-{nonce}";
        var marker = $"bowire-will-{nonce}";
        var payload = $"{{\"bowire-probe\":\"{marker}\"}}";

        var factory = new MqttClientFactory();
        try
        {
            using var subscriber = factory.CreateMqttClient();
            var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            subscriber.ApplicationMessageReceivedAsync += e =>
            {
                var text = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());
                if (text.Contains(marker, StringComparison.Ordinal)) delivered.TrySetResult(true);
                return Task.CompletedTask;
            };

            // Subscribe FIRST — the will isn't retained, so the subscriber must
            // be listening before the will fires.
            await subscriber.ConnectAsync(BuildOptions(host, port, will: false, willTopic, payload), ct).ConfigureAwait(false);
            await subscriber.SubscribeAsync(
                new MqttTopicFilterBuilder().WithTopic(willTopic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(),
                ct).ConfigureAwait(false);

            // Connect a second client that carries the malicious will, then drop
            // it UNGRACEFULLY — dispose the client without a clean DISCONNECT so
            // the broker sees an abnormal disconnect and fires the will. A clean
            // DISCONNECT (the normal path) suppresses the will, which is exactly
            // why the shared plugin's graceful teardown can't exercise this.
            var willClient = factory.CreateMqttClient();
            await willClient.ConnectAsync(BuildOptions(host, port, will: true, willTopic, payload), ct).ConfigureAwait(false);
            willClient.Dispose(); // abrupt teardown → broker publishes the will

            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromSeconds(3));
            var got = await WaitForDeliveryAsync(delivered.Task, window.Token).ConfigureAwait(false);

            try { await subscriber.DisconnectAsync(cancellationToken: ct).ConfigureAwait(false); }
            catch (Exception) { /* best-effort */ }

            return got
                ? [Finding("BWR-OWASP-API8-MQTT-WILL-ABUSE", "MQTT broker delivers unfiltered will messages",
                    $"A malicious Last-Will-and-Testament planted on '{willTopic}' was delivered, unfiltered, to a subscriber when the will client's session dropped. A connecting client can plant a message that lands on other clients' topics on disconnect — a trust-boundary / message-injection vector.",
                    "Restrict will-message publishing with a topic ACL (deny wills on topics the client can't publish to), and validate / scope will topics. Mosquitto: per-client ACLs applied to the will topic.",
                    "medium", 5.9)]
                : [Marker(ScanFindingStatus.Safe, "API8-MQTT-WILL-CLEAN",
                    "Will message not delivered",
                    "The will client connected + dropped, but a subscriber did not receive the will payload — the broker filtered / did not publish the unauthorised will.")];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker(ScanFindingStatus.Skipped, "API8-MQTT-WILL-INCONCLUSIVE",
                "MQTT will-message probe inconclusive",
                $"The will-message probe could not complete ({ex.GetType().Name}) — broker unreachable, rejected the connect, or doesn't support MQTT-5 disconnect-with-will.")];
        }
    }

    private static async Task<bool> WaitForDeliveryAsync(Task<bool> delivered, CancellationToken window)
    {
        try
        {
            var done = await Task.WhenAny(delivered, Task.Delay(Timeout.Infinite, window)).ConfigureAwait(false);
            return done == delivered && await delivered.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false; // window elapsed with no delivery
        }
    }

    private static MqttClientOptions BuildOptions(string host, int port, bool will, string willTopic, string willPayload)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithClientId($"bowire-{Guid.NewGuid():N}"[..24])
            .WithCleanSession(true)
            .WithTimeout(TimeSpan.FromSeconds(10));
        if (will)
        {
            builder = builder
                .WithWillTopic(willTopic)
                .WithWillPayload(Encoding.UTF8.GetBytes(willPayload))
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        }
        return builder.Build();
    }

    private static bool TryParseBroker(string target, out string host, out int port)
    {
        host = "";
        port = 1883;
        if (string.IsNullOrWhiteSpace(target)) return false;
        var i = target.IndexOf("://", StringComparison.Ordinal);
        if (i <= 0 || !s_brokerSchemes.Contains(target[..i], StringComparer.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return false;
        host = uri.Host;
        if (uri.Port > 0) port = uri.Port;
        return true;
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
            remediation: "Diagnostic marker for the active MQTT will-message probe."),
        Status = status,
        Detail = detail,
    };
}
