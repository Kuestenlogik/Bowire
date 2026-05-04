// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Protocol.Sse;

/// <summary>
/// Connects to an SSE endpoint and reads events as they arrive.
/// Parses the <c>text/event-stream</c> format (id, event, data, retry fields).
/// </summary>
internal sealed class SseSubscriber : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    /// <summary>
    /// Pass an externally-built <see cref="HttpClient"/> (typically from
    /// <c>BowireHttpClientFactory.Create</c> in the plugin's
    /// <c>Initialize</c>) so localhost-cert trust opt-in flows through.
    /// SSE connections are long-lived, so the caller is expected to
    /// configure the timeout — this constructor does not override it.
    /// </summary>
    public SseSubscriber(HttpClient client)
    {
        _client = client;
        _ownsClient = false;
    }

    /// <summary>
    /// Default constructor — builds a vanilla 1-hour-timeout HttpClient.
    /// Used by test paths that don't have a configuration to plumb through.
    /// </summary>
    public SseSubscriber()
    {
        _client = new HttpClient
        {
            Timeout = TimeSpan.FromHours(1), // SSE connections are long-lived
        };
        _ownsClient = true;
    }

    /// <summary>
    /// Subscribe to an SSE endpoint and yield parsed events as JSON strings.
    /// </summary>
    public async IAsyncEnumerable<string> SubscribeAsync(
        string url,
        Dictionary<string, string>? headers = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (headers is not null)
        {
            foreach (var (key, value) in headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? eventType = null;
        string? eventId = null;
        int? retry = null;
        var dataLines = new List<string>();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break; // Stream ended

            if (line.Length == 0)
            {
                // Empty line = event boundary — dispatch accumulated event
                if (dataLines.Count > 0)
                {
                    var data = string.Join("\n", dataLines);
                    var evt = new SseEventPayload(eventId, eventType, data, retry);
                    yield return JsonSerializer.Serialize(evt, s_jsonOptions);

                    eventType = null;
                    eventId = null;
                    retry = null;
                    dataLines.Clear();
                }

                continue;
            }

            // Lines starting with ':' are comments — ignore
            if (line.StartsWith(':'))
                continue;

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLines.Add(line.Length > 5 ? line[5..].TrimStart() : "");
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line[6..].TrimStart();
            }
            else if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                eventId = line[3..].TrimStart();
            }
            else if (line.StartsWith("retry:", StringComparison.Ordinal))
            {
                if (int.TryParse(line[6..].TrimStart(), out var retryMs))
                    retry = retryMs;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClient) _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
