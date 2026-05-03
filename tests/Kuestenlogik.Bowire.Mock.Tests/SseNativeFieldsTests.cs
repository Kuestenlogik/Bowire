// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Phase-2 deferred: SSE native `id:` / `event:` / `retry:` fields.
/// When a recording carries the SseEventPayload envelope captured by
/// SseSubscriber, the mock unwraps it and emits native SSE field
/// lines rather than wrapping the whole envelope inside one `data:`
/// line. Non-envelope payloads still fall through to the legacy
/// `id: &lt;index&gt;\ndata: &lt;raw&gt;` shape so pre-Phase-2
/// recordings keep replaying.
/// </summary>
public sealed class SseNativeFieldsTests
{
    [Fact]
    public async Task Envelope_WithIdEventRetryData_EmitsNativeFieldLines()
    {
        // Captured SseEventPayload: the recorder parsed the original
        // server's SSE frames, so "Id", "Event", "Data", "Retry" are
        // the same values the original server wrote on the wire.
        var rec = BuildRecording(new Dictionary<string, object?>
        {
            ["Id"] = "42",
            ["Event"] = "price-update",
            ["Data"] = "{\"symbol\":\"KL\",\"price\":101.5}",
            ["Retry"] = 1500
        });
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/events", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("id: 42\n", body, StringComparison.Ordinal);
        Assert.Contains("event: price-update\n", body, StringComparison.Ordinal);
        Assert.Contains("retry: 1500\n", body, StringComparison.Ordinal);
        Assert.Contains("data: {\"symbol\":\"KL\",\"price\":101.5}\n\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Envelope_MissingId_FallsBackToFrameIndex()
    {
        // Server never set `id:` on the wire. The envelope captures a
        // null Id. Replay falls back to the recorder-assigned frame
        // index so Last-Event-ID resume still works downstream.
        var rec = BuildRecording(new Dictionary<string, object?>
        {
            ["Id"] = null,
            ["Event"] = null,
            ["Data"] = "tick",
            ["Retry"] = null
        });
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/events", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("id: 0\n", body, StringComparison.Ordinal);
        Assert.Contains("data: tick\n\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain("event:", body, StringComparison.Ordinal);
        Assert.DoesNotContain("retry:", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Envelope_CamelCaseKeys_AlsoRecognised()
    {
        // The SseEventPayload record serialises with PascalCase, but
        // third-party producers might feed camelCase-shaped envelopes.
        // Both spellings unwrap correctly.
        var rec = BuildRecording(new Dictionary<string, object?>
        {
            ["id"] = "abc",
            ["event"] = "note",
            ["data"] = "hello"
        });
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/events", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("id: abc\n", body, StringComparison.Ordinal);
        Assert.Contains("event: note\n", body, StringComparison.Ordinal);
        Assert.Contains("data: hello\n\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSseFrame_MultiLineData_EmitsOneDataLinePerSource()
    {
        // SSE spec: multi-line data gets one `data:` line per source
        // line. The client reassembles them with newlines in between.
        var envelope = """{"Data":"line one\nline two\nline three"}""";
        var text = Replay.UnaryReplayer.FormatSseFrame(frameIndex: 7, envelope);

        Assert.Contains("id: 7\n", text, StringComparison.Ordinal);
        Assert.Contains("data: line one\n", text, StringComparison.Ordinal);
        Assert.Contains("data: line two\n", text, StringComparison.Ordinal);
        Assert.Contains("data: line three\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSseFrame_NonEnvelopePayload_FallsBackToLegacyShape()
    {
        // Pre-Phase-2 recordings store raw JSON payloads (not wrapped
        // in the SseEventPayload envelope). The legacy `id: <index>\n
        // data: <raw>\n\n` shape keeps them replaying.
        var text = Replay.UnaryReplayer.FormatSseFrame(frameIndex: 3, "{\"n\":3}");
        Assert.Equal("id: 3\ndata: {\"n\":3}\n\n", text);
    }

    [Fact]
    public void FormatSseFrame_NonObjectPayload_EmitsDataLineVerbatim()
    {
        // Arrays, numbers, strings — anything that isn't a JSON object
        // skips the envelope-check path entirely.
        var arrayText = Replay.UnaryReplayer.FormatSseFrame(frameIndex: 1, "[1,2,3]");
        Assert.Equal("id: 1\ndata: [1,2,3]\n\n", arrayText);

        var numberText = Replay.UnaryReplayer.FormatSseFrame(frameIndex: 2, "42");
        Assert.Equal("id: 2\ndata: 42\n\n", numberText);
    }

    // ---- helpers ----

    private static BowireRecording BuildRecording(Dictionary<string, object?> envelope)
    {
        // The recorder persists the envelope as a JsonElement (via
        // JSON.parse on the browser side, surfaced through System.Text.Json).
        // Build the same shape here: serialise the dictionary, then parse
        // back into a JsonElement so the frame data matches production.
        var json = System.Text.Json.JsonSerializer.Serialize(envelope);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var element = doc.RootElement.Clone();

        return new BowireRecording
        {
            Id = "rec_sse_native",
            Name = "sse-native-fields",
            RecordingFormatVersion = 2,
            Steps =
            {
                new BowireRecordingStep
                {
                    Id = "step_events",
                    Protocol = "rest",
                    Service = "Events",
                    Method = "Stream",
                    MethodType = "ServerStreaming",
                    HttpPath = "/events",
                    HttpVerb = "GET",
                    Status = "OK",
                    ReceivedMessages = new List<BowireRecordingFrame>
                    {
                        new() { Index = 0, TimestampMs = 0, Data = element }
                    }
                }
            }
        };
    }

    private static IHost BuildHost(BowireRecording recording) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                    .Configure(app =>
                    {
                        app.UseBowireMock(recording, opts =>
                        {
                            opts.Watch = false;
                            opts.ReplaySpeed = 0;
                        });
                    });
            })
            .Start();
}
