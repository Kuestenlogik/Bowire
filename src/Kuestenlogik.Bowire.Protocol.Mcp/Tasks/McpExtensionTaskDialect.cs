// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tasks;

/// <summary>
/// The <c>io.modelcontextprotocol/tasks</c> extension, schema 2026-07-28
/// (#801), on the official <c>ModelContextProtocol.Extensions.Tasks</c>
/// package. The package owns the per-request opt-in
/// (<c>_meta["io.modelcontextprotocol/clientCapabilities"]</c>) and the
/// polymorphic <c>tools/call</c> result; Bowire only maps what comes back.
/// </summary>
/// <remarks>
/// Bowire's client declares no other extension for the session, which is
/// what keeps csharp-sdk#1920 out of the way: a session-level extension
/// overwrites the per-request Tasks opt-in, and the call never becomes a
/// task. https://github.com/modelcontextprotocol/csharp-sdk/issues/1920
/// </remarks>
internal sealed class McpExtensionTaskDialect : IMcpTaskDialect
{
    public static readonly McpExtensionTaskDialect Instance = new();

    // The spec names the notification `notifications/tasks`; SDK 2.2.0's
    // TasksProtocol.NotificationTaskStatus is `notifications/tasks/status`,
    // the 2025-11-25 name. Servers built on either send one or the other,
    // so Bowire listens for both.
    private static readonly string[] s_statusNotifications = ["notifications/tasks", TasksProtocol.NotificationTaskStatus];

    // Serialized through the package's own type infos, never as `object`:
    // only those carry the converter that writes the TTL as integer
    // milliseconds. The plain context options write it as a TimeSpan
    // string, and the TTL is lost on the way to the frame.

    public string Shape => McpTaskShapes.Extension;

    /// <summary>Whether the server advertised the extension in its capabilities.</summary>
    public static bool IsAdvertisedBy(McpClient client) =>
        client.ServerCapabilities?.Extensions?.ContainsKey(TasksProtocol.ExtensionId) == true;

    public async Task<McpToolCallOutcome> CallToolAsync(
        McpClient client, string toolName, IDictionary<string, JsonElement>? arguments, ProgressToken? progressToken, CancellationToken ct)
    {
        // ProgressToken is read from _meta; the package adds its opt-in
        // to the same object.
        var request = new CallToolRequestParams { Name = toolName, Arguments = arguments };
        if (progressToken is { } token)
            request.Meta = new JsonObject { ["progressToken"] = JsonSerializer.SerializeToNode(token, McpJsonUtilities.DefaultOptions) };
        var outcome = await client.CallToolAsTaskAsync(request, ct).ConfigureAwait(false);

        return outcome.IsTask
            ? new McpToolCallOutcome(null, Map(JsonSerializer.SerializeToElement(outcome.TaskCreated!, McpTasksJsonContext.Default.CreateTaskResult)))
            : new McpToolCallOutcome(JsonSerializer.SerializeToElement(outcome.Result, McpJsonUtilities.DefaultOptions), null);
    }

    public async Task<McpTaskState> GetAsync(McpClient client, string taskId, CancellationToken ct)
    {
        var task = await client.GetTaskAsync(taskId, ct).ConfigureAwait(false);
        return Map(JsonSerializer.SerializeToElement(task, McpTasksJsonContext.Default.GetTaskResult));
    }

    public async Task UpdateAsync(McpClient client, string taskId, JsonElement inputResponses, CancellationToken ct)
    {
        var responses = inputResponses.Deserialize(McpTasksJsonContext.Default.IDictionaryStringInputResponse)
            ?? throw new ArgumentException("inputResponses must be an object keyed like the task's inputRequests.", nameof(inputResponses));
        await client.UpdateTaskAsync(
            new UpdateTaskRequestParams { TaskId = taskId, InputResponses = responses }, ct).ConfigureAwait(false);
    }

    public async Task CancelAsync(McpClient client, string taskId, CancellationToken ct) =>
        await client.CancelTaskAsync(taskId, ct).ConfigureAwait(false);

    /// <summary>
    /// <c>subscriptions/listen</c> with the task's id. The request stays open
    /// for as long as Bowire follows the task; the server acknowledges with
    /// the task ids it agreed to push, and only an agreement for this id
    /// counts. A server that does not know the method, or acknowledges
    /// without this id, leaves Bowire polling.
    /// </summary>
    public async Task<McpTaskListener> ListenAsync(
        McpClient client, string taskId, Action<McpTaskState> onStatus, CancellationToken ct)
    {
        var acknowledged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlers = new List<IAsyncDisposable>();
        foreach (var method in s_statusNotifications)
        {
            handlers.Add(client.RegisterNotificationHandler(method, (notification, _) =>
            {
                if (notification.Params is JsonObject p && TryMap(p) is { } state && state.TaskId == taskId)
                    onStatus(state);
                return ValueTask.CompletedTask;
            }));
        }
        handlers.Add(client.RegisterNotificationHandler(NotificationMethods.SubscriptionsAcknowledgedNotification, (notification, _) =>
        {
            acknowledged.TrySetResult(AcknowledgesTask(notification.Params, taskId));
            return ValueTask.CompletedTask;
        }));

        var listening = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var request = new JsonRpcRequest
        {
            Method = RequestMethods.SubscriptionsListen,
            Params = new JsonObject
            {
                ["notifications"] = new JsonObject { ["taskIds"] = new JsonArray(taskId) },
                // The extension has to be declared on this request too; a
                // server answers a listen for task ids without it -32021.
                ["_meta"] = new JsonObject
                {
                    ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject
                    {
                        ["extensions"] = new JsonObject { [TasksProtocol.ExtensionId] = new JsonObject() },
                    },
                },
            },
        };
        var stream = Task.Run(async () =>
        {
            try { await client.SendRequestAsync(request, listening.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is McpException or HttpRequestException or OperationCanceledException) { }
            // However the request ends — refused, closed, cancelled — an
            // acknowledgement that has not arrived is not coming.
            acknowledged.TrySetResult(false);
        }, CancellationToken.None);

        var subscribed = await acknowledged.Task.WaitAsync(ct).ConfigureAwait(false);
        return new McpTaskListener(subscribed, new Stop(listening, stream, handlers), subscribed ? stream : null);
    }

    private static bool AcknowledgesTask(JsonNode? ack, string taskId) =>
        ack?["notifications"]?["taskIds"] is JsonArray ids
        && ids.Any(id => id?.GetValueKind() == JsonValueKind.String && id.GetValue<string>() == taskId);

    private static McpTaskState? TryMap(JsonObject task)
    {
        try { return Map(JsonSerializer.SerializeToElement(task)); }
        catch (JsonException) { return null; }
    }

    private static McpTaskState Map(JsonElement task) => McpTaskJson.Parse(McpTaskShapes.Extension, task);

    private sealed class Stop(CancellationTokenSource listening, Task stream, List<IAsyncDisposable> handlers) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await listening.CancelAsync().ConfigureAwait(false);
            await stream.ConfigureAwait(false);
            listening.Dispose();
            foreach (var handler in handlers) await handler.DisposeAsync().ConfigureAwait(false);
        }
    }
}
