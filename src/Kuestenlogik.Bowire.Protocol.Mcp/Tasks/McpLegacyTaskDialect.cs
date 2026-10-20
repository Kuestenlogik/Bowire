// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tasks;

/// <summary>
/// Tasks as the 2025-11-25 core spec defined them, experimentally (#801):
/// a tool declares <c>execution.taskSupport</c>, the client adds
/// <c>task: {}</c> to <c>tools/call</c>, the server answers
/// <c>{ task: {…} }</c>, and the result is fetched with
/// <c>tasks/result</c> once the task is terminal. Servers built on that
/// revision are still in use; SDK 2.2.0 no longer models any of it, so
/// this speaks raw JSON-RPC over the SDK's session.
/// </summary>
/// <remarks>
/// <para>
/// Input requests do not travel in the task here: the server sends them as
/// ordinary <c>elicitation/create</c> requests while the client blocks on
/// <c>tasks/result</c>. Answering those is not part of this slice —
/// <see cref="UpdateAsync"/> says so rather than pretending, and the
/// <c>input_required</c> status still shows.
/// </para>
/// <para>
/// The server capability <c>tasks.requests.tools.call</c> arrives in the
/// initialize result, which the SDK consumes without keeping the
/// <c>tasks</c> member. The tool-level <c>execution.taskSupport</c> is
/// what Bowire can read, so a tool that declares <c>optional</c> or
/// <c>required</c> is taken as the server's statement that it runs tasks.
/// </para>
/// </remarks>
internal sealed class McpLegacyTaskDialect : IMcpTaskDialect
{
    public static readonly McpLegacyTaskDialect Instance = new();

    public string Shape => McpTaskShapes.Legacy;

    /// <summary>Whether the session negotiated the revision that has these tasks.</summary>
    public static bool IsNegotiatedBy(McpClient client) =>
        string.Equals(client.NegotiatedProtocolVersion, McpTaskShapes.Legacy, StringComparison.Ordinal);

    /// <summary>
    /// <c>execution.taskSupport</c> per tool name, from a raw
    /// <c>tools/list</c> walk — the SDK's <see cref="Tool"/> drops the
    /// member. Tools without it are absent (they are <c>forbidden</c>).
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ReadTaskSupportAsync(McpClient client, CancellationToken ct)
    {
        var support = new Dictionary<string, string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await SendAsync(client, RequestMethods.ToolsList,
                cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor }, ct).ConfigureAwait(false);
            if (page?["tools"] is JsonArray tools)
            {
                foreach (var tool in tools.OfType<JsonObject>())
                {
                    if (tool["name"]?.GetValue<string>() is { } name
                        && tool["execution"]?["taskSupport"] is JsonValue value
                        && value.TryGetValue<string>(out var mode)
                        && mode is "optional" or "required")
                    {
                        support[name] = mode;
                    }
                }
            }
            cursor = page?["nextCursor"] is JsonValue next && next.TryGetValue<string>(out var c) && c.Length > 0 ? c : null;
        }
        while (cursor is not null);
        return support;
    }

    public async Task<McpToolCallOutcome> CallToolAsync(
        McpClient client, string toolName, IDictionary<string, JsonElement>? arguments, ProgressToken? progressToken, CancellationToken ct)
    {
        var args = new JsonObject();
        if (arguments is not null)
        {
            foreach (var (key, value) in arguments) args[key] = JsonNode.Parse(value.GetRawText());
        }

        // No ttl: the receiver picks one and reports it on the task.
        var parameters = new JsonObject { ["name"] = toolName, ["arguments"] = args, ["task"] = new JsonObject() };
        // 2025-11-25: the call's progress token stays valid for the
        // task's whole lifetime.
        if (progressToken is { } token)
            parameters["_meta"] = new JsonObject { ["progressToken"] = JsonSerializer.SerializeToNode(token, McpJsonUtilities.DefaultOptions) };
        var result = await SendAsync(client, RequestMethods.ToolsCall, parameters, ct).ConfigureAwait(false);

        var element = JsonSerializer.SerializeToElement(result);
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty("task", out var task)
            ? new McpToolCallOutcome(null, McpTaskJson.Parse(Shape, task))
            : new McpToolCallOutcome(element, null);
    }

    public async Task<McpTaskState> GetAsync(McpClient client, string taskId, CancellationToken ct)
    {
        var result = await SendAsync(client, "tasks/get", new JsonObject { ["taskId"] = taskId }, ct).ConfigureAwait(false);
        var state = McpTaskJson.Parse(Shape, JsonSerializer.SerializeToElement(result));
        return state.Status is McpTaskStatuses.Completed or McpTaskStatuses.Failed
            ? await WithOutcomeAsync(client, state, ct).ConfigureAwait(false)
            : state;
    }

    /// <summary>
    /// A terminal task's payload sits behind <c>tasks/result</c>, which
    /// returns "exactly what the underlying request would have returned":
    /// the tool result (a <c>failed</c> task may carry one with
    /// <c>isError</c>), or the JSON-RPC error.
    /// </summary>
    private static async Task<McpTaskState> WithOutcomeAsync(McpClient client, McpTaskState state, CancellationToken ct)
    {
        try
        {
            var result = await SendAsync(client, "tasks/result", new JsonObject { ["taskId"] = state.TaskId }, ct).ConfigureAwait(false);
            return state with { Result = JsonSerializer.SerializeToElement(result) };
        }
        catch (McpProtocolException ex)
        {
            return state with
            {
                Error = JsonSerializer.SerializeToElement(new { code = (int)ex.ErrorCode, message = ex.Message }),
            };
        }
    }

    public Task UpdateAsync(McpClient client, string taskId, JsonElement inputResponses, CancellationToken ct) =>
        throw new NotSupportedException(
            "This server speaks MCP tasks as of 2025-11-25, where input reaches a task as an elicitation/create "
            + "request during tasks/result rather than through tasks/update. Bowire does not answer those yet.");

    public async Task CancelAsync(McpClient client, string taskId, CancellationToken ct) =>
        await SendAsync(client, "tasks/cancel", new JsonObject { ["taskId"] = taskId }, ct).ConfigureAwait(false);

    /// <summary>
    /// 2025-11-25 has no subscription: a receiver MAY send
    /// <c>notifications/tasks/status</c> on any stream, and a requestor MUST
    /// NOT rely on it. So Bowire takes what arrives and keeps polling.
    /// </summary>
    public Task<McpTaskListener> ListenAsync(McpClient client, string taskId, Action<McpTaskState> onStatus, CancellationToken ct)
    {
        var handler = client.RegisterNotificationHandler("notifications/tasks/status", (notification, _) =>
        {
            if (notification.Params is JsonObject p)
            {
                try
                {
                    var state = McpTaskJson.Parse(McpTaskShapes.Legacy, JsonSerializer.SerializeToElement(p));
                    if (state.TaskId == taskId) onStatus(state);
                }
                catch (JsonException) { /* not a task; nothing to show */ }
            }
            return ValueTask.CompletedTask;
        });
        return Task.FromResult(new McpTaskListener(subscribed: false, handler));
    }

    private static async Task<JsonNode?> SendAsync(McpClient client, string method, JsonObject parameters, CancellationToken ct)
    {
        var response = await client.SendRequestAsync(new JsonRpcRequest { Method = method, Params = parameters }, ct).ConfigureAwait(false);
        return response.Result;
    }
}
