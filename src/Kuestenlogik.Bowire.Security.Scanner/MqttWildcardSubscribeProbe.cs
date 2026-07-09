// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Active MQTT probe (#396), rolling up to <c>API1:2023 — Broken Object Level
/// Authorization</c>. With an authenticated client (<c>--auth-header</c>),
/// subscribes to the multi-level wildcard <c>#</c> and observes what the broker
/// actually <i>delivers</i> over a bounded window — not just whether the SUBACK
/// was granted (many brokers grant <c>#</c> but still ACL-filter delivery, so
/// SUBACK alone is a weak signal).
///
/// <para>The verdict is delivery-based and keyed on the operator-supplied
/// expected-topic scope (<c>--active-expected-topic</c>): any topic delivered
/// to the <c>#</c> subscription that falls <b>outside</b> that scope is
/// over-broad access — cross-tenant eavesdropping / topic-level authz failure.
/// Retained messages count: they're delivered to a fresh <c>#</c> subscriber
/// immediately, so the probe is meaningful even against a quiet broker. Without
/// a scope the probe can't render a pass/fail verdict — it reports what it
/// observed (distinct topic count) so the operator can judge.</para>
///
/// <para>Read-only (subscribe + observe, never publishes), but aggressive
/// (broad subscription held open for the observation window), so it runs only
/// under <c>--active</c>.</para>
/// </summary>
internal sealed class MqttWildcardSubscribeProbe : IActiveProtocolProbe
{
    public OwaspApiEntry Entry { get; } = OwaspApiCatalog.Entries.Single(e => e.Id == "API1:2023");

    public string ProtocolId => "mqtt";

    private static readonly string[] s_brokerSchemes = ["mqtt", "mqtts", "tcp", "ssl"];

    // Cap the observation window so a generous --active-duration can't stall the
    // scan on this one probe; retained delivery is immediate on subscribe.
    private const int MaxObserveSeconds = 30;

    public async Task<IReadOnlyList<ScanFinding>> RunAsync(
        string target, IBowireProtocol protocol, IList<string> authHeaders, ActiveScanOptions active, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(active);
        // Needs an authenticated baseline (the point is "what does *this* client
        // get to see") and an MQTT broker target.
        if (authHeaders.Count == 0 || !LooksLikeBroker(target)) return [];

        var observed = new HashSet<string>(StringComparer.Ordinal);
        var seconds = Math.Clamp(active.DurationSeconds, 1, MaxObserveSeconds);
        try
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromSeconds(seconds));
            try
            {
                await foreach (var frame in protocol.InvokeStreamAsync(
                    target, service: "", method: "#", [], showInternalServices: false, metadata: null, window.Token)
                    .ConfigureAwait(false))
                {
                    var topic = ExtractTopic(frame);
                    if (topic is not null) observed.Add(topic);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Observation window elapsed — expected for a live `#` stream.
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [Marker(ScanFindingStatus.Skipped, "API1-MQTT-WILDCARD-INCONCLUSIVE",
                "MQTT wildcard-subscribe probe inconclusive",
                $"Could not observe a '#' subscription ({ex.GetType().Name}) — the broker rejected the wildcard subscribe or was unreachable; over-broad access not determined.")];
        }

        // No scope supplied → observation-only, no pass/fail verdict.
        if (active.ExpectedTopics.Count == 0)
        {
            return [Marker(ScanFindingStatus.Skipped, "API1-MQTT-WILDCARD-NO-SCOPE",
                "MQTT wildcard-subscribe: no expected-topic scope supplied",
                $"Subscribed to '#' and observed {observed.Count} distinct topic(s) over {seconds}s. Supply --active-expected-topic <filter> (the topics this client is meant to reach) to get an over-broad-access verdict.")];
        }

        var outOfScope = observed.Where(t => !InScope(t, active.ExpectedTopics)).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        if (outOfScope.Length > 0)
        {
            var sample = string.Join(", ", outOfScope.Take(8));
            return [Finding("BWR-OWASP-API1-MQTT-WILDCARD-OVERBROAD", "MQTT broker delivers topics outside the client's scope",
                $"An authenticated '#' subscription received {outOfScope.Length} topic(s) outside the supplied scope — e.g. {sample}. The broker's ACL does not confine this client to its own topics, so it can eavesdrop on other tenants' / devices' traffic (topic-level authorization failure).",
                "Enforce a per-client topic ACL that scopes subscriptions to the client's own topics, and deny (or narrow) multi-level wildcard (`#`) subscribes. Mosquitto: per-user ACL entries; managed brokers: client/tenant topic namespaces.",
                "high", 7.1)];
        }

        return [Marker(ScanFindingStatus.Safe, "API1-MQTT-WILDCARD-SCOPED",
            "MQTT wildcard subscribe stays within scope",
            $"Subscribed to '#' and every delivered topic ({observed.Count} distinct) fell within the supplied expected-topic scope — no over-broad delivery observed over {seconds}s.")];
    }

    private static string? ExtractTopic(string frame)
    {
        try
        {
            using var doc = JsonDocument.Parse(frame);
            return doc.RootElement.TryGetProperty("topic", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool InScope(string topic, IReadOnlyList<string> expected)
    {
        foreach (var filter in expected)
            if (TopicFilterMatches(filter, topic)) return true;
        return false;
    }

    /// <summary>MQTT topic-filter match: <c>+</c> = one level, <c>#</c> = rest (trailing only).</summary>
    internal static bool TopicFilterMatches(string filter, string topic)
    {
        if (string.IsNullOrEmpty(filter) || string.IsNullOrEmpty(topic)) return false;
        var f = filter.Split('/');
        var t = topic.Split('/');
        for (var i = 0; i < f.Length; i++)
        {
            if (f[i] == "#") return true; // matches this level and everything below
            if (i >= t.Length) return false;
            if (f[i] == "+") continue;    // single-level wildcard
            if (!string.Equals(f[i], t[i], StringComparison.Ordinal)) return false;
        }
        return f.Length == t.Length;
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
        Template = SyntheticTemplate.Build(id, name, cwe: "CWE-284", owaspApi: Entry.Tag, severity, cvss, remediation),
        Status = ScanFindingStatus.Vulnerable,
        Detail = detail,
    };

    private ScanFinding Marker(ScanFindingStatus status, string id, string name, string detail) => new()
    {
        Template = SyntheticTemplate.Build(id, name, cwe: null, owaspApi: Entry.Tag, severity: "info", cvss: null,
            remediation: "Diagnostic marker for the active MQTT wildcard-subscribe probe."),
        Status = status,
        Detail = detail,
    };
}
