// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Monitoring.PagerDuty;

/// <summary>
/// Triggers / resolves a PagerDuty incident on a probe transition (Events API
/// v2). A failing edge sends <c>event_action: trigger</c>; a recovery sends
/// <c>resolve</c>. Both carry the same <c>dedup_key</c> (the probe name) so the
/// recovery clears the incident the alert opened. Delivery failures are wrapped
/// in <see cref="SignalerException"/> so the runner logs and skips them.
/// </summary>
public sealed class PagerDutySignaler : ISignaler
{
    /// <summary>The PagerDuty Events API v2 enqueue endpoint.</summary>
    public static readonly Uri DefaultEndpoint = new("https://events.pagerduty.com/v2/enqueue");

    private readonly HttpClient _http;
    private readonly string _routingKey;
    private readonly Uri _endpoint;

    public PagerDutySignaler(string routingKey, HttpClient http, Uri? endpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
        _routingKey = routingKey;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _endpoint = endpoint ?? DefaultEndpoint;
    }

    /// <inheritdoc/>
    public async Task DeliverAsync(SignalEvent signal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var trigger = signal.Transition == ProbeTransition.ToFailing;
        var dedupKey = "bowire-monitor/" + signal.Probe.Name;

        object body = trigger
            ? new
            {
                routing_key = _routingKey,
                event_action = "trigger",
                dedup_key = dedupKey,
                payload = new
                {
                    summary = $"{signal.Probe.Name} went failing — {signal.Outcome.Result}",
                    severity = Severity(signal.Probe.Severity),
                    source = "bowire-monitor",
                },
            }
            : new
            {
                routing_key = _routingKey,
                event_action = "resolve",
                dedup_key = dedupKey,
            };

        var payload = JsonSerializer.Serialize(body);
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            throw new SignalerException($"PagerDuty enqueue failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SignalerException($"PagerDuty enqueue timed out: {ex.Message}", ex);
        }
    }

    private static string Severity(ProbeSeverity severity) => severity switch
    {
        ProbeSeverity.Crit => "critical",
        ProbeSeverity.Info => "info",
        _ => "warning",
    };
}

/// <summary>Contributes the <c>pagerduty</c> <c>--signal</c> scheme.</summary>
public sealed class PagerDutySignalerFactory : ISignalerFactory
{
    private static readonly HttpClient SharedClient = new();

    /// <inheritdoc/>
    public string Scheme => "pagerduty";

    /// <inheritdoc/>
    public ISignaler Create(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new SignalerConfigException(
                "pagerduty signal needs a routing key: --signal pagerduty:<integration-routing-key>");
        }
        return new PagerDutySignaler(argument, SharedClient);
    }
}
