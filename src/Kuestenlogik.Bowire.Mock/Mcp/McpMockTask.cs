// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuestenlogik.Bowire.Mock.Mcp;

/// <summary>
/// A task the mock serves from a recorded task call (#803), in the shape of
/// the <c>io.modelcontextprotocol/tasks</c> extension. The recorded statuses
/// come in their recorded order and at their recorded pace (scaled by the
/// replay speed; 0 replays at once). At an <c>input_required</c> with
/// requests not answered yet it waits for <c>tasks/update</c>, then goes on
/// from where the recording received its answer; <c>tasks/cancel</c> ends it
/// <c>cancelled</c>. Nothing runs in the background: the state is worked out
/// when a client asks for it.
/// </summary>
internal sealed class McpMockTask
{
    private static readonly string[] Terminal = ["completed", "failed", "cancelled"];

    private readonly List<McpRecordedFrame> _timeline;
    private readonly double _speed;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _answered = new(StringComparer.Ordinal);
    private int _cursor;
    private DateTimeOffset _anchorTime;
    private long _anchorOffset;
    private bool _waitingForInput;
    private JsonObject _state;
    private JsonNode? _result;
    private JsonNode? _error;

    public McpMockTask(string taskId, McpRecordedCall call, double speed, TimeProvider clock)
    {
        TaskId = taskId;
        _speed = speed;
        _clock = clock;
        Created = clock.GetUtcNow();
        _anchorTime = Created;

        // The timeline starts at the recorded task's first state; what came
        // before it (the call itself) is already behind the client.
        var frames = call.Frames.ToList();
        var first = frames.FindIndex(f => f.Event == "task");
        var created = frames[first];
        _anchorOffset = created.OffsetMs;
        _timeline = frames.Skip(first + 1).ToList();
        // ttlMs is null for an unlimited task: only a number is a TTL.
        TtlMs = created.Data.TryGetProperty("ttlMs", out var ttl) && ttl.ValueKind == JsonValueKind.Number && ttl.TryGetInt64(out var t) ? t : null;
        PollIntervalMs = created.Data.TryGetProperty("pollIntervalMs", out var poll) && poll.ValueKind == JsonValueKind.Number && poll.TryGetInt64(out var p) ? p : null;
        _state = new JsonObject { ["status"] = "working" };
        Apply(created, Created);
    }

    public string TaskId { get; }
    public DateTimeOffset Created { get; }
    public long? TtlMs { get; }
    public long? PollIntervalMs { get; }

    /// <summary>The task as <c>tasks/get</c> answers it now; with its result or error once it ended.</summary>
    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            Advance(_clock.GetUtcNow());
            var task = new JsonObject
            {
                ["taskId"] = TaskId,
                ["status"] = _state["status"]?.DeepClone(),
                ["createdAt"] = Iso(Created),
                ["lastUpdatedAt"] = _state["lastUpdatedAt"]?.DeepClone() ?? Iso(Created),
            };
            // An unlimited task carries no ttlMs: the SDK's task models refuse a null.
            if (TtlMs is { } ttl) task["ttlMs"] = ttl;
            if (PollIntervalMs is { } poll) task["pollIntervalMs"] = poll;
            if (_state["statusMessage"] is { } message) task["statusMessage"] = message.DeepClone();
            if (_state["inputRequests"] is JsonObject requests)
            {
                var open = new JsonObject();
                foreach (var (key, value) in requests)
                    if (!_answered.Contains(key)) open[key] = value?.DeepClone();
                if (open.Count > 0) task["inputRequests"] = open;
            }
            if (_result is not null) task["result"] = _result.DeepClone();
            if (_error is not null) task["error"] = _error.DeepClone();
            return task;
        }
    }

    /// <summary>
    /// <c>tasks/update</c>: take the answers to the waiting requests and go
    /// on. Answers to keys the task never asked are refused: the reason, or
    /// <see langword="null"/> when they were taken.
    /// </summary>
    public string? Answer(JsonElement inputResponses)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Advance(now);
            var asked = _state["inputRequests"] as JsonObject;
            foreach (var response in inputResponses.EnumerateObject())
            {
                if (asked is null || !asked.ContainsKey(response.Name))
                    return $"Task '{TaskId}' has no input request '{response.Name}'.";
            }
            foreach (var response in inputResponses.EnumerateObject()) _answered.Add(response.Name);
            if (!_waitingForInput || asked!.Any(kv => !_answered.Contains(kv.Key))) return null;

            // Go on from where the recording received its answer.
            _waitingForInput = false;
            var next = _timeline.Skip(_cursor).FirstOrDefault(f => f.Event == "input");
            _anchorOffset = next?.OffsetMs ?? (_cursor < _timeline.Count ? _timeline[_cursor].OffsetMs : _anchorOffset);
            _anchorTime = now;
            _state["lastUpdatedAt"] = Iso(now);
            return null;
        }
    }

    /// <summary><c>tasks/cancel</c>: a task still running ends <c>cancelled</c>.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            Advance(_clock.GetUtcNow());
            if (IsTerminal) return;
            _state["status"] = "cancelled";
            _state["statusMessage"] = "Cancelled on request.";
            _state.Remove("inputRequests");
            _state["lastUpdatedAt"] = Iso(_clock.GetUtcNow());
            _cursor = _timeline.Count;
        }
    }

    private bool IsTerminal => Terminal.Contains(_state["status"]?.GetValue<string>());

    // Apply every recorded frame that is due, and stop at a question nobody answered yet.
    private void Advance(DateTimeOffset now)
    {
        while (_cursor < _timeline.Count && !_waitingForInput && !IsTerminal)
        {
            var frame = _timeline[_cursor];
            if (_speed > 0)
            {
                var elapsed = _anchorOffset + (now - _anchorTime).TotalMilliseconds * _speed;
                if (elapsed < frame.OffsetMs) break;
            }
            _cursor++;
            Apply(frame, now);
        }
    }

    private void Apply(McpRecordedFrame frame, DateTimeOffset now)
    {
        switch (frame.Event)
        {
            case "task":
            {
                var data = frame.Data;
                var status = data.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (status is null) return;
                _state["status"] = status;
                _state["lastUpdatedAt"] = Iso(now);
                if (data.TryGetProperty("statusMessage", out var message) && message.ValueKind == JsonValueKind.String) _state["statusMessage"] = message.GetString();
                else _state.Remove("statusMessage");
                if (data.TryGetProperty("inputRequests", out var requests) && requests.ValueKind == JsonValueKind.Object)
                {
                    _state["inputRequests"] = JsonNode.Parse(requests.GetRawText());
                    // Wait only for a question not answered yet: a poll the
                    // recording made before the server took the answer
                    // still shows the old one.
                    if (status == "input_required" && requests.EnumerateObject().Any(r => !_answered.Contains(r.Name)))
                        _waitingForInput = true;
                }
                else
                {
                    _state.Remove("inputRequests");
                }
                if (Terminal.Contains(status)) TakeOutcome();
                break;
            }
            case "result":
                _result ??= JsonNode.Parse(frame.Data.GetRawText());
                break;
            case "error":
                _error ??= ErrorOf(frame.Data);
                break;
        }
    }

    // A recorded terminal status is followed by the result or error frame:
    // attach it now, so no poll sees a completed task without its result.
    private void TakeOutcome()
    {
        for (var i = _cursor; i < _timeline.Count; i++)
        {
            var frame = _timeline[i];
            if (frame.Event == "result") { _result ??= JsonNode.Parse(frame.Data.GetRawText()); break; }
            if (frame.Event == "error") { _error ??= ErrorOf(frame.Data); break; }
        }
        _cursor = _timeline.Count;
    }

    // The plugin's error frame wraps the JSON-RPC error the task failed with.
    private static JsonNode? ErrorOf(JsonElement data) =>
        data.TryGetProperty("error", out var inner) && inner.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(inner.GetRawText())
            : JsonNode.Parse(data.GetRawText());

    private static string Iso(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);
}
