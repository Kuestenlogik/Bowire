// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Mock.Mcp;

/// <summary>One frame of a recorded MCP tool call, as the plugin streamed it: its kind, data and time.</summary>
internal sealed record McpRecordedFrame(string Event, JsonElement Data, long OffsetMs);

/// <summary>
/// A recorded MCP tool call (#803) — a step the workbench wrote for
/// <c>Tools/&lt;tool&gt;</c>: either a stream of the plugin's
/// <c>{ "event", "data" }</c> frames (<c>task</c>, <c>input</c>,
/// <c>progress</c>, <c>log</c>, <c>result</c>, <c>error</c>) with their times,
/// or a unary call with the result as its response.
/// </summary>
internal sealed class McpRecordedCall
{
    private McpRecordedCall(BowireRecordingStep step, string tool, List<McpRecordedFrame> frames)
    {
        Step = step;
        Tool = tool;
        Frames = frames;
    }

    public BowireRecordingStep Step { get; }
    public string Tool { get; }
    public IReadOnlyList<McpRecordedFrame> Frames { get; }

    /// <summary>Whether the server answered this call with a task.</summary>
    public bool IsTask => Frames.Any(f => f.Event == "task");

    /// <summary>The tool's result, from the last <c>result</c> frame.</summary>
    public JsonElement? Result => Frames.LastOrDefault(f => f.Event == "result")?.Data;

    /// <summary>The error the call ended in, from the last <c>error</c> frame.</summary>
    public JsonElement? Error => Frames.LastOrDefault(f => f.Event == "error")?.Data;

    /// <summary>
    /// A tool-level MCP step: protocol <c>mcp</c>, service <c>Tools</c>, and no
    /// HTTP route of its own (a step with one is a wire-level capture the
    /// REST and SSE replay already serve).
    /// </summary>
    public static bool IsToolStep(BowireRecordingStep step) =>
        string.Equals(step.Protocol, "mcp", StringComparison.OrdinalIgnoreCase)
        && string.Equals(step.Service, "Tools", StringComparison.Ordinal)
        && !string.IsNullOrEmpty(step.Method)
        && string.IsNullOrEmpty(step.HttpPath);

    public static McpRecordedCall From(BowireRecordingStep step)
    {
        var frames = new List<McpRecordedFrame>();
        if (step.ReceivedMessages is { Count: > 0 } received)
        {
            foreach (var frame in received.OrderBy(f => f.Index))
            {
                if (Parse(frame.Data) is { } parsed)
                    frames.Add(new McpRecordedFrame(parsed.Event, parsed.Data, frame.TimestampMs ?? 0));
            }
        }
        else if (!string.IsNullOrWhiteSpace(step.Response))
        {
            // A unary call: the response pane's payload is the tool's result.
            try
            {
                using var doc = JsonDocument.Parse(step.Response);
                frames.Add(new McpRecordedFrame("result", doc.RootElement.Clone(), step.DurationMs));
            }
            catch (JsonException) { /* not a result this replay can serve */ }
        }
        return new McpRecordedCall(step, step.Method!, frames);
    }

    /// <summary>The recorded arguments (the request body), for choosing among several calls of one tool.</summary>
    public JsonElement? Arguments
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Step.Body)) return null;
            try
            {
                using var doc = JsonDocument.Parse(Step.Body);
                return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
            }
            catch (JsonException) { return null; }
        }
    }

    private static (string Event, JsonElement Data)? Parse(object? data)
    {
        JsonElement element;
        switch (data)
        {
            case JsonElement { ValueKind: JsonValueKind.String } text:
                if (!TryParse(text.GetString(), out element)) return null;
                break;
            case JsonElement json:
                element = json;
                break;
            case string text:
                if (!TryParse(text, out element)) return null;
                break;
            default:
                return null;
        }
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("event", out var kind) || kind.ValueKind != JsonValueKind.String
            || !element.TryGetProperty("data", out var payload))
        {
            return null;
        }
        return (kind.GetString()!, payload.Clone());
    }

    private static bool TryParse(string? text, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }
}
