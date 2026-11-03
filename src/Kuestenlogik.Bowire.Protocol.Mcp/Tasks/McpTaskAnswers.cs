// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tasks;

/// <summary>
/// Tells the streams that follow a task in this process about each answer
/// <c>Tasks/update</c> delivered to it (#803). The answer travels on its own
/// call, so without this the stream — and with it a recording of the call —
/// would show the task asking and then going on, but not what it was told.
/// The stream writes the answer as an <c>input</c> frame; the mock replays a
/// recorded task by it.
/// </summary>
internal sealed class McpTaskAnswers
{
    private readonly ConcurrentDictionary<(string Origin, string TaskId), List<Action<JsonElement>>> _listeners = new();
    private readonly Lock _gate = new();

    /// <summary>Hear about the answers to one task until the returned handle is disposed.</summary>
    public IDisposable Listen(string origin, string taskId, Action<JsonElement> onAnswer)
    {
        var key = (origin, taskId);
        lock (_gate) _listeners.GetOrAdd(key, _ => []).Add(onAnswer);
        return new Stop(() =>
        {
            lock (_gate)
            {
                if (!_listeners.TryGetValue(key, out var list)) return;
                list.Remove(onAnswer);
                if (list.Count == 0) _listeners.TryRemove(key, out _);
            }
        });
    }

    /// <summary>An answer reached the task: pass it to whoever follows it here.</summary>
    public void Delivered(string origin, string taskId, JsonElement inputResponses)
    {
        Action<JsonElement>[] listeners;
        lock (_gate)
        {
            if (!_listeners.TryGetValue((origin, taskId), out var list)) return;
            listeners = [.. list];
        }
        var copy = inputResponses.Clone();
        foreach (var listener in listeners) listener(copy);
    }

    private sealed class Stop(Action stop) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) stop(); }
    }
}
