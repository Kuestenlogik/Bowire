// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Monitoring.Slack;

/// <summary>
/// Posts a probe transition to a Slack incoming webhook as a chat message.
/// A failing edge reads as an alert, a passing edge as a recovery. Delivery
/// failures are wrapped in <see cref="SignalerException"/> so the runner logs
/// and skips them without aborting the run.
/// </summary>
public sealed class SlackSignaler : ISignaler
{
    private readonly HttpClient _http;
    private readonly Uri _webhook;

    public SlackSignaler(Uri webhook, HttpClient http)
    {
        _webhook = webhook ?? throw new ArgumentNullException(nameof(webhook));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <inheritdoc/>
    public async Task DeliverAsync(SignalEvent signal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var failing = signal.Transition == ProbeTransition.ToFailing;
        var emoji = failing ? ":rotating_light:" : ":white_check_mark:";
        var verb = failing ? "went failing" : "recovered";
        var text = $"{emoji} *{signal.Probe.Name}* [{signal.Probe.Severity}] {verb} — {signal.Outcome.Result}";
        var payload = JsonSerializer.Serialize(new { text });

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_webhook, content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            throw new SignalerException($"Slack webhook failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SignalerException($"Slack webhook timed out: {ex.Message}", ex);
        }
    }
}

/// <summary>Contributes the <c>slack</c> <c>--signal</c> scheme.</summary>
public sealed class SlackSignalerFactory : ISignalerFactory
{
    // One client for the process lifetime — the recommended HttpClient pattern.
    private static readonly HttpClient SharedClient = new();

    /// <inheritdoc/>
    public string Scheme => "slack";

    /// <inheritdoc/>
    public ISignaler Create(string argument)
    {
        if (!Uri.TryCreate(argument, UriKind.Absolute, out var webhook)
            || (webhook.Scheme != Uri.UriSchemeHttps && webhook.Scheme != Uri.UriSchemeHttp))
        {
            throw new SignalerConfigException(
                "slack signal needs a webhook URL: --signal slack:https://hooks.slack.com/services/...");
        }
        return new SlackSignaler(webhook, SharedClient);
    }
}
