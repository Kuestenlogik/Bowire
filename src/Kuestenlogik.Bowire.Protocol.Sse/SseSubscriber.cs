// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Kuestenlogik.Bowire;

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
        // SSE connections are long-lived.
        _client = Kuestenlogik.Bowire.Net.BowireHttpClientFactory.Create(null, "sse", TimeSpan.FromHours(1));
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
            foreach (var (key, value) in BowireMetadataKeys.WireHeaders(headers))
                request.Headers.TryAddWithoutValidation(key, value);
            Kuestenlogik.Bowire.Auth.BowireHttpAuth.Attach(request, headers);
        }

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Guard the content type: a genuine SSE endpoint MUST answer
        // text/event-stream (per the EventSource spec). A plain 2xx page
        // (HTML / JSON) would otherwise be parsed line-by-line and silently
        // yield nothing — surface an explicit error instead so callers (the
        // workbench, and the API2 SSE security probe) can tell "not an
        // event-stream" apart from "stream open, no events yet".
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            // #712 - protocol, not transport: the request succeeded and the
            // peer answered, it just did not answer with an event stream.
            yield return BowireStreamErrorEnvelope.Frame(
                BowireStreamErrorKinds.Protocol,
                $"not an event-stream (content-type: {mediaType ?? "none"})");
            yield break;
        }

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

                    eventId = null;
                    retry = null;
                }

                // The spec resets the data and event-type buffers at EVERY
                // blank line, so the type of a block without data cannot
                // label the next event (#863).
                eventType = null;
                dataLines.Clear();
                continue;
            }

            // Lines starting with ':' are comments — ignore
            if (line.StartsWith(':'))
                continue;

            // #863 — field and value as the spec splits them: at the first
            // colon, or the whole line is the field name with an empty value
            // (a bare "data" line is an empty data line, not noise). Exactly
            // one leading space is dropped from the value; the rest of its
            // whitespace is content.
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];

            switch (field)
            {
                case "data":
                    dataLines.Add(value);
                    break;
                case "event":
                    eventType = value;
                    break;
                case "id" when !value.Contains('\0', StringComparison.Ordinal):
                    eventId = value;
                    break;
                // ASCII digits only, per the spec — no sign, no whitespace.
                case "retry" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var retryMs):
                    retry = retryMs;
                    break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClient) _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
