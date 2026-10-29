// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tasks;

/// <summary>
/// One spec shape of MCP tasks (#801): how a tool call opts in, how a task
/// is read, answered and cancelled. Everything above this seam — following
/// a task, framing it for the workbench — is shared, so the two shapes
/// differ only where the wire does.
/// </summary>
internal interface IMcpTaskDialect
{
    /// <summary><see cref="McpTaskShapes.Extension"/> or <see cref="McpTaskShapes.Legacy"/>.</summary>
    string Shape { get; }

    /// <summary>
    /// Call a tool so the server may answer with a task. The outcome is
    /// either the ordinary result or the created task — the server decides.
    /// <paramref name="progressToken"/>, when given, rides on the call so a
    /// server can report progress against it, as on any tool call.
    /// </summary>
    Task<McpToolCallOutcome> CallToolAsync(
        McpClient client, string toolName, IDictionary<string, JsonElement>? arguments, ProgressToken? progressToken, CancellationToken ct);

    /// <summary>The task's current state, with its result or error once terminal.</summary>
    Task<McpTaskState> GetAsync(McpClient client, string taskId, CancellationToken ct);

    /// <summary>Answer outstanding <c>inputRequests</c>; <paramref name="inputResponses"/> is keyed like them.</summary>
    Task UpdateAsync(McpClient client, string taskId, JsonElement inputResponses, CancellationToken ct);

    /// <summary>Ask the server to cancel. Cooperative: the task may still end otherwise.</summary>
    Task CancelAsync(McpClient client, string taskId, CancellationToken ct);

    /// <summary>
    /// Listen for status notifications of one task. The returned handle
    /// stops listening; <see cref="McpTaskListener.Subscribed"/> completes
    /// with <see langword="true"/> once the server has agreed to send them,
    /// which is what lets the follower stop polling.
    /// </summary>
    Task<McpTaskListener> ListenAsync(McpClient client, string taskId, Action<McpTaskState> onStatus, CancellationToken ct);
}

/// <summary>What a task-capable tool call came back with.</summary>
internal sealed record McpToolCallOutcome(JsonElement? Result, McpTaskState? Task);

/// <summary>A running status listener; disposing it stops listening.</summary>
internal sealed class McpTaskListener : IAsyncDisposable
{
    private readonly IAsyncDisposable? _handle;
    private readonly Action<McpTaskState>? _observe;

    public McpTaskListener(bool subscribed, IAsyncDisposable? handle, Task? ended = null, Action<McpTaskState>? observe = null)
    {
        Subscribed = subscribed;
        _handle = handle;
        Ended = ended ?? new TaskCompletionSource().Task;
        _observe = observe;
    }

    /// <summary>
    /// Whether the server agreed to push status for this task. When it did
    /// not, notifications may still arrive, but they cannot be relied on and
    /// the follower keeps polling.
    /// </summary>
    public bool Subscribed { get; }

    /// <summary>
    /// Completes when the subscription stream ends on its own — the server
    /// closed it — after which nothing more will be pushed and the follower
    /// goes back to polling. Never completes for a listener that did not
    /// subscribe.
    /// </summary>
    public Task Ended { get; }

    /// <summary>
    /// The follower reports each state it adopts here, so a listener can act
    /// on one — the 2025-11-25 shape fetches the task's questions once it
    /// turns <c>input_required</c> (#821).
    /// </summary>
    public void Observe(McpTaskState state) => _observe?.Invoke(state);

    public ValueTask DisposeAsync() => _handle?.DisposeAsync() ?? ValueTask.CompletedTask;
}
