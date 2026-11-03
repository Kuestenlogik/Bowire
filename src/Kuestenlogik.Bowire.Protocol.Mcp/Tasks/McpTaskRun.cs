// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tasks;

/// <summary>
/// <c>Tasks/run</c> (#803): call a tool as a task and drive it to its end in
/// one unary call — the step a flow or <c>bowire test</c> takes to test a
/// server's task lifecycle. It answers input requests with the responses it
/// was given, cancels when the task reaches the status it was told to, gives
/// up after <c>timeoutMs</c>, and answers with what happened: every status
/// the task passed through, its status messages, the keys it answered, and
/// the result or error it ended in. A flow asserts on those with its
/// ordinary expectations (<c>$.status</c>, <c>$.statuses</c>, <c>$.result</c>).
/// </summary>
/// <remarks>
/// Everything happens in one call because neither the flow runner nor
/// <c>bowire test</c> carries a value from one step to the next, so a
/// <c>taskId</c> from one step could not reach another. It also keeps a
/// 2025-11-25 task answerable: its questions reach only the client that
/// follows it (#821), which here is this call.
/// </remarks>
internal static class McpTaskRun
{
    /// <summary>The statuses <c>cancelAt</c> may name: the ones a task can still leave.</summary>
    private static readonly string[] CancelStatuses = [McpTaskStatuses.Working, McpTaskStatuses.InputRequired];

    /// <summary>The key of an input response that answers any request without one of its own.</summary>
    internal const string AnyRequest = "*";

    public static async Task<JsonElement> RunAsync(
        McpTaskService tasks, McpClient client, string serverUrl,
        Func<McpClientOptions, CancellationToken, Task<McpClient>>? connect,
        JsonElement body, CancellationToken ct)
    {
        var request = Read(body);
        var dialect = await tasks.ForToolAsync(client, serverUrl, request.Tool, ct, connect).ConfigureAwait(false);
        if (dialect is null)
        {
            // Not a task-capable tool on this server: run it as it is, and say so.
            var plain = await client.CallToolAsync(request.Tool, ToArguments(request.Arguments), cancellationToken: ct).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(new { ranAsTask = false, tool = request.Tool, result = plain });
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.TimeoutMs is { } ms) limit.CancelAfter(TimeSpan.FromMilliseconds(ms));

        var run = new Track();
        try
        {
            var outcome = await dialect.CallToolAsync(client, request.Tool, request.Arguments, progressToken: null, limit.Token).ConfigureAwait(false);
            if (outcome.Task is not { } created)
                return JsonSerializer.SerializeToElement(new { ranAsTask = false, tool = request.Tool, result = outcome.Result });
            run.TaskId = created.TaskId;

            await foreach (var step in McpTaskFollower.FollowAsync(client, dialect, created.TaskId, created, ct: limit.Token).ConfigureAwait(false))
            {
                if (step.State is { } state) run.Saw(state);
                switch (step.Kind)
                {
                    case "task" when step.State is { } s:
                        if (request.CancelAt == s.Status && !run.CancelRequested)
                        {
                            run.CancelRequested = true;
                            await dialect.CancelAsync(client, s.TaskId, limit.Token).ConfigureAwait(false);
                        }
                        else if (s.Status == McpTaskStatuses.InputRequired && s.InputRequests is { ValueKind: JsonValueKind.Object } requests)
                        {
                            await AnswerAsync(dialect, client, s.TaskId, requests, request.InputResponses, run.Answered, limit.Token).ConfigureAwait(false);
                        }
                        break;
                    case "result":
                        run.Result = step.Data;
                        break;
                    case "error" when step.State is null:
                        // Not the task's outcome: the follow itself failed (TTL passed, task purged, unreadable).
                        throw new InvalidOperationException(Message(step.Data) ?? $"Following task '{created.TaskId}' failed.");
                    case "error":
                        run.Error = step.Data;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(run.TaskId is null
                ? $"Calling '{request.Tool}' did not return within {request.TimeoutMs} ms."
                : $"Task '{run.TaskId}' is still '{run.Status ?? "unknown"}' after {request.TimeoutMs} ms (statuses so far: {string.Join(" → ", run.Statuses)}).");
        }

        var answer = new JsonObject
        {
            ["ranAsTask"] = true,
            ["tool"] = request.Tool,
            ["taskId"] = run.TaskId,
            ["shape"] = dialect.Shape,
            ["status"] = run.Status,
            ["statusMessage"] = run.StatusMessage,
            ["statuses"] = new JsonArray([.. run.Statuses.Select(s => (JsonNode)s)]),
            ["statusMessages"] = new JsonArray([.. run.StatusMessages.Select(m => (JsonNode)m)]),
            ["answered"] = new JsonArray([.. run.Answered.Select(k => (JsonNode)k)]),
            ["cancelRequested"] = run.CancelRequested,
        };
        if (run.Result is { } r) answer["result"] = JsonNode.Parse(r.GetRawText());
        if (run.Error is { } e) answer["error"] = JsonNode.Parse(e.GetRawText());
        return JsonSerializer.SerializeToElement(answer);
    }

    /// <summary>What a run saw of its task, in order.</summary>
    private sealed class Track
    {
        public string? TaskId { get; set; }
        public string? Status { get; private set; }
        public string? StatusMessage { get; private set; }
        public List<string> Statuses { get; } = [];
        public List<string> StatusMessages { get; } = [];
        public List<string> Answered { get; } = [];
        public bool CancelRequested { get; set; }
        public JsonElement? Result { get; set; }
        public JsonElement? Error { get; set; }

        /// <summary>Record a state: a status or message only when it differs from the one before.</summary>
        public void Saw(McpTaskState state)
        {
            Status = state.Status;
            StatusMessage = state.StatusMessage;
            if (Statuses.Count == 0 || Statuses[^1] != state.Status) Statuses.Add(state.Status);
            if (state.StatusMessage is { Length: > 0 } message && (StatusMessages.Count == 0 || StatusMessages[^1] != message)) StatusMessages.Add(message);
        }
    }

    /// <summary>
    /// Answer the outstanding requests not answered yet: each with the response
    /// under its own key, else under <c>*</c>. One without either stops the run
    /// with a message naming it — waiting would only end in the timeout.
    /// </summary>
    private static async Task AnswerAsync(
        IMcpTaskDialect dialect, McpClient client, string taskId, JsonElement requests,
        JsonElement? responses, List<string> answered, CancellationToken ct)
    {
        var batch = new JsonObject();
        foreach (var pending in requests.EnumerateObject())
        {
            if (answered.Contains(pending.Name)) continue;
            JsonElement response = default;
            var found = responses is { ValueKind: JsonValueKind.Object } given
                && (given.TryGetProperty(pending.Name, out response) || given.TryGetProperty(AnyRequest, out response));
            if (!found)
            {
                var method = pending.Value.TryGetProperty("method", out var m) ? m.GetString() : "a request";
                var text = pending.Value.TryGetProperty("params", out var p) && p.TryGetProperty("message", out var msg) ? $" (\"{msg.GetString()}\")" : "";
                throw new InvalidOperationException(
                    $"Task '{taskId}' asks for input '{pending.Name}' ({method}{text}), and the run has no response for it. "
                    + $"Add one to inputResponses under \"{pending.Name}\", or under \"{AnyRequest}\" for any request.");
            }
            batch[pending.Name] = JsonNode.Parse(response.GetRawText());
        }
        if (batch.Count == 0) return;
        await dialect.UpdateAsync(client, taskId, JsonSerializer.SerializeToElement(batch), ct).ConfigureAwait(false);
        answered.AddRange(batch.Select(kv => kv.Key));
    }

    private sealed record Request(
        string Tool, IDictionary<string, JsonElement>? Arguments, long? TimeoutMs, JsonElement? InputResponses, string? CancelAt);

    private static Request Read(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("tool", out var tool) || tool.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tool.GetString()))
        {
            throw new ArgumentException("Tasks/run needs { \"tool\": \"…\" } — the name of the tool to call as a task.");
        }

        IDictionary<string, JsonElement>? arguments = null;
        if (body.TryGetProperty("arguments", out var args) && args.ValueKind != JsonValueKind.Null)
        {
            if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tasks/run: arguments is an object of the tool's arguments.");
            arguments = args.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        }

        long? timeoutMs = null;
        if (body.TryGetProperty("timeoutMs", out var timeout) && timeout.ValueKind != JsonValueKind.Null)
        {
            if (!timeout.TryGetInt64(out var ms) || ms <= 0) throw new ArgumentException("Tasks/run: timeoutMs is a positive number of milliseconds.");
            timeoutMs = ms;
        }

        JsonElement? responses = null;
        if (body.TryGetProperty("inputResponses", out var given) && given.ValueKind != JsonValueKind.Null)
        {
            if (given.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Tasks/run: inputResponses is an object keyed like the task's inputRequests (or \"*\" for any).");
            responses = given.Clone();
        }

        string? cancelAt = null;
        if (body.TryGetProperty("cancelAt", out var at) && at.ValueKind != JsonValueKind.Null)
        {
            cancelAt = at.GetString();
            if (!CancelStatuses.Contains(cancelAt))
                throw new ArgumentException($"Tasks/run: cancelAt is one of {string.Join(", ", CancelStatuses)} — the status at which to ask the server to cancel.");
        }

        return new Request(tool.GetString()!, arguments, timeoutMs, responses, cancelAt);
    }

    private static Dictionary<string, object?>? ToArguments(IDictionary<string, JsonElement>? arguments) =>
        arguments?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);

    private static string? Message(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
}
