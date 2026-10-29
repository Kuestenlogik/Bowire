// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tasks;

/// <summary>
/// The input requests of 2025-11-25 tasks that wait for an answer in this
/// process (#821). In that shape a server asks with an ordinary
/// <c>elicitation/create</c> on the session that blocks in
/// <c>tasks/result</c>, so the request lives on that session and only this
/// process can answer it. <c>Tasks/update</c> hands the answer over here;
/// the waiting handler takes it — a handshake, nothing polls.
/// </summary>
/// <remarks>
/// Keyed by server origin and task id: task ids are the server's, and two
/// servers may hand out the same one. The key of each request is Bowire's —
/// the SDK hands an elicitation handler the params, not the JSON-RPC id —
/// and unique per task, which is all the workbench's task card needs.
/// </remarks>
internal sealed class McpLegacyTaskInputs
{
    private readonly ConcurrentDictionary<(string Origin, string TaskId), Waiting> _tasks = new();

    private sealed class Waiting
    {
        public readonly ConcurrentDictionary<string, Entry> Requests = new(StringComparer.Ordinal);
        public int Next;
    }

    private sealed record Entry(string Method, JsonNode? Params, TaskCompletionSource<JsonElement> Answer);

    /// <summary>Register one request; the returned task completes with the response <c>Tasks/update</c> brings.</summary>
    public (string Key, Task<JsonElement> Answer) Add(string origin, string taskId, string method, JsonNode? parameters)
    {
        var waiting = _tasks.GetOrAdd((origin, taskId), _ => new Waiting());
        var key = method.Split('/')[0] + "-" + Interlocked.Increment(ref waiting.Next).ToString(CultureInfo.InvariantCulture);
        var entry = new Entry(method, parameters, new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously));
        waiting.Requests[key] = entry;
        return (key, entry.Answer.Task);
    }

    /// <summary>Drop a request once it was answered or its wait ended.</summary>
    public void Remove(string origin, string taskId, string key)
    {
        if (_tasks.TryGetValue((origin, taskId), out var waiting)) waiting.Requests.TryRemove(key, out _);
    }

    /// <summary>Forget a task nobody follows any more, unless a request of it still waits.</summary>
    public void Forget(string origin, string taskId)
    {
        if (_tasks.TryGetValue((origin, taskId), out var waiting) && waiting.Requests.IsEmpty)
            _tasks.TryRemove(KeyValuePair.Create((origin, taskId), waiting));
    }

    /// <summary>The waiting requests in the task frame's shape — <c>{ key: { method, params } }</c> — or <see langword="null"/>.</summary>
    public JsonElement? Snapshot(string origin, string taskId)
    {
        if (!_tasks.TryGetValue((origin, taskId), out var waiting) || waiting.Requests.IsEmpty) return null;
        var node = new JsonObject();
        foreach (var (key, entry) in waiting.Requests.OrderBy(r => r.Key, StringComparer.Ordinal))
            node[key] = new JsonObject { ["method"] = entry.Method, ["params"] = entry.Params?.DeepClone() };
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>
    /// Hand <paramref name="inputResponses"/> to the waiting requests. Every
    /// key is checked before any is answered, so a typo answers nothing.
    /// </summary>
    public void Answer(string origin, string taskId, JsonElement inputResponses)
    {
        if (!_tasks.TryGetValue((origin, taskId), out var waiting) || waiting.Requests.IsEmpty)
        {
            throw new InvalidOperationException(
                $"Task '{taskId}' has no input request waiting in this Bowire. Under MCP 2025-11-25 the server asks the client "
                + "that follows the task, on the session that waits in tasks/result, so only that client can answer: "
                + "answer from the stream that follows the task (the tool call or Tasks/follow) while it is open.");
        }

        var answers = new List<(Entry Entry, JsonElement Response)>();
        foreach (var response in inputResponses.EnumerateObject())
        {
            if (!waiting.Requests.TryGetValue(response.Name, out var entry))
            {
                throw new ArgumentException(
                    $"Task '{taskId}' has no input request '{response.Name}' waiting. Waiting: {string.Join(", ", waiting.Requests.Keys.Order(StringComparer.Ordinal))}.");
            }
            if (entry.Method == RequestMethods.ElicitationCreate) CheckElicitResult(response.Name, response.Value);
            answers.Add((entry, response.Value.Clone()));
        }
        foreach (var (entry, response) in answers) entry.Answer.TrySetResult(response);
    }

    private static void CheckElicitResult(string key, JsonElement response)
    {
        ElicitResult? result = null;
        try { result = response.Deserialize<ElicitResult>(McpJsonUtilities.DefaultOptions); }
        catch (JsonException) { /* reported below */ }
        if (result?.Action is not ("accept" or "decline" or "cancel"))
        {
            throw new ArgumentException(
                $"The response to '{key}' is not an elicitation result: it needs \"action\" (accept, decline or cancel) and, to accept, \"content\".");
        }
    }
}
