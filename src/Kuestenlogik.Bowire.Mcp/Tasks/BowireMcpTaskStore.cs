// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Auth;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Mcp.Tasks;

/// <summary>
/// The durable task store behind Bowire's MCP server (#802). Every state
/// change is written to <c>~/.bowire/mcp-tasks/&lt;id&gt;.json</c>, so a
/// finished task's result survives the agent disconnecting and the server
/// restarting; <see cref="TimeToLive"/> bounds how long it is kept.
/// <para>
/// A task that is still working when the process ends cannot finish. Read
/// back after a restart, it is reported as failed with that reason rather
/// than left working forever. Input requests (elicitation from a running
/// task) belong to a live waiter and are kept in memory only.
/// </para>
/// <para>
/// Beyond <see cref="IMcpTaskStore"/>, the store carries what a Bowire tool
/// needs while it runs as a task: <see cref="CurrentTaskId"/> and
/// <see cref="SetStatusMessageAsync"/> for progress such as <c>step 3/12</c>.
/// </para>
/// </summary>
public sealed class BowireMcpTaskStore : IMcpTaskStore
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // The id of the task the calling flow runs in. CreateTaskAsync sets it
    // synchronously, so it lands in the execution context the Tasks filter
    // hands to the background run that follows it.
    private static readonly AsyncLocal<string?> s_currentTaskId = new();

    private readonly string? _root;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, McpTaskInfo> _live = new(StringComparer.Ordinal);

    /// <param name="root">The folder for the task files; when null, the per-user <c>mcp-tasks</c> folder of the calling user, resolved on each access.</param>
    public BowireMcpTaskStore(string? root = null)
    {
        _root = root;
    }

    private string Root => _root ?? BowireUserContext.GetUserPath("mcp-tasks");

    /// <summary>How long a task and its result are kept after creation. Default 24 hours.</summary>
    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The poll interval suggested to clients. Default one second.</summary>
    public long PollIntervalMs { get; init; } = 1000;

    /// <summary>The task the calling code runs as, or null outside a task.</summary>
    public static string? CurrentTaskId => s_currentTaskId.Value;

    /// <inheritdoc />
    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived;

    /// <inheritdoc />
    // Deliberately not async: the AsyncLocal set here must stay in the
    // caller's execution context (see s_currentTaskId).
    public Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var info = new McpTaskInfo(Guid.NewGuid().ToString("N"), McpTaskStatus.Working, now, now, TimeToLive, PollIntervalMs);
        lock (_gate)
        {
            _live[info.TaskId] = info;
            Persist(info);
        }
        s_currentTaskId.Value = info.TaskId;
        return Task.FromResult(info);
    }

    /// <inheritdoc />
    public Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Load(taskId));
        }
    }

    /// <summary>Set the message a client sees for a working task, e.g. <c>step 3/12</c>. Returns the updated task, or null.</summary>
    public Task<McpTaskInfo?> SetStatusMessageAsync(string taskId, string message, CancellationToken cancellationToken = default) =>
        Task.FromResult(Update(taskId, e => e.Status is McpTaskStatus.Working or McpTaskStatus.InputRequired
            ? e with { StatusMessage = message, LastUpdatedAt = DateTimeOffset.UtcNow }
            : e));

    /// <inheritdoc />
    public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default)
    {
        Update(taskId, e => IsTerminal(e.Status) ? e : e with { Status = McpTaskStatus.Completed, Result = result, LastUpdatedAt = DateTimeOffset.UtcNow }, finished: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken = default)
    {
        Update(taskId, e => IsTerminal(e.Status) ? e : e with { Status = McpTaskStatus.Failed, Error = error, LastUpdatedAt = DateTimeOffset.UtcNow }, finished: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var transitioned = false;
        Update(taskId, e =>
        {
            if (IsTerminal(e.Status)) return e;
            transitioned = true;
            return e with { Status = McpTaskStatus.Cancelled, LastUpdatedAt = DateTimeOffset.UtcNow };
        }, finished: true);
        return Task.FromResult(transitioned);
    }

    /// <inheritdoc />
    public Task SetInputRequestsAsync(string taskId, IDictionary<string, InputRequest> inputRequests, CancellationToken cancellationToken = default)
    {
        Update(taskId, e =>
        {
            if (IsTerminal(e.Status)) return e;
            var requests = new Dictionary<string, InputRequest>(e.InputRequests ?? new Dictionary<string, InputRequest>(), StringComparer.Ordinal);
            foreach (var (key, value) in inputRequests) requests[key] = value;
            return e with { InputRequests = requests, Status = McpTaskStatus.InputRequired, LastUpdatedAt = DateTimeOffset.UtcNow };
        });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResolveInputRequestsAsync(string taskId, IDictionary<string, InputResponse> inputResponses, CancellationToken cancellationToken = default)
    {
        var wasTerminal = false;
        Update(taskId, e =>
        {
            if (IsTerminal(e.Status))
            {
                wasTerminal = true;
                return e;
            }
            var requests = new Dictionary<string, InputRequest>(e.InputRequests ?? new Dictionary<string, InputRequest>(), StringComparer.Ordinal);
            foreach (var key in inputResponses.Keys) requests.Remove(key);
            return e with
            {
                InputRequests = requests,
                Status = requests.Count == 0 ? McpTaskStatus.Working : e.Status,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            };
        });
        if (wasTerminal) return Task.CompletedTask;

        foreach (var (requestId, response) in inputResponses)
        {
            InputResponseReceived?.Invoke(new InputResponseReceivedEventArgs { TaskId = taskId, RequestId = requestId, Response = response });
        }
        return Task.CompletedTask;
    }

    // ---- state ----

    private McpTaskInfo? Update(string taskId, Func<McpTaskInfo, McpTaskInfo> change, bool finished = false)
    {
        lock (_gate)
        {
            var current = Load(taskId);
            if (current is null) return null;
            var next = change(current);
            if (finished && IsTerminal(next.Status)) _live.Remove(taskId);
            else if (!IsTerminal(next.Status)) _live[taskId] = next;
            Persist(next);
            return next;
        }
    }

    // Call under _gate.
    private McpTaskInfo? Load(string taskId)
    {
        var now = DateTimeOffset.UtcNow;
        if (_live.TryGetValue(taskId, out var live))
        {
            if (!IsExpired(live, now)) return live;
            _live.Remove(taskId);
            Delete(taskId);
            return null;
        }

        var path = PathOf(taskId);
        if (path is null || !File.Exists(path)) return null;
        StoredTask? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredTask>(File.ReadAllText(path), s_json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (stored is null) return null;

        var info = stored.ToInfo();
        if (IsExpired(info, now))
        {
            Delete(taskId);
            return null;
        }
        if (!IsTerminal(info.Status))
        {
            // Not running in this process: the server stopped before it finished.
            var error = JsonSerializer.SerializeToElement(
                new JsonRpcErrorDetail { Code = (int)McpErrorCode.InternalError, Message = "The Bowire MCP server stopped before this task finished." },
                McpJsonUtilities.DefaultOptions.GetTypeInfo<JsonRpcErrorDetail>());
            info = info with { Status = McpTaskStatus.Failed, Error = error, LastUpdatedAt = now };
            Persist(info);
        }
        return info;
    }

    private void Persist(McpTaskInfo info)
    {
        var path = PathOf(info.TaskId);
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Root);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(StoredTask.From(info), s_json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The live copy still answers; only durability across a restart is lost.
        }
    }

    private void Delete(string taskId)
    {
        var path = PathOf(taskId);
        try { if (path is not null) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Task ids are the store's own GUIDs; anything else never names a file.
    private string? PathOf(string taskId) =>
        taskId.Length == 32 && taskId.All(char.IsAsciiHexDigit) ? Path.Combine(Root, taskId + ".json") : null;

    private static bool IsTerminal(McpTaskStatus status) =>
        status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled;

    private static bool IsExpired(McpTaskInfo info, DateTimeOffset now) =>
        info.TimeToLive is { } ttl && ttl > TimeSpan.Zero && now - info.CreatedAt >= ttl;

    // The file shape: the task without its in-memory input requests.
    private sealed record StoredTask(
        string TaskId, McpTaskStatus Status, DateTimeOffset CreatedAt, DateTimeOffset LastUpdatedAt,
        long? TimeToLiveMs, long? PollIntervalMs, string? StatusMessage, JsonElement? Result, JsonElement? Error)
    {
        public static StoredTask From(McpTaskInfo i) => new(
            i.TaskId, i.Status, i.CreatedAt, i.LastUpdatedAt,
            i.TimeToLive is { } ttl ? (long)ttl.TotalMilliseconds : null,
            i.PollIntervalMs, i.StatusMessage, i.Result, i.Error);

        public McpTaskInfo ToInfo() => new(
            TaskId, Status, CreatedAt, LastUpdatedAt,
            TimeToLiveMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
            PollIntervalMs, StatusMessage, Result, Error, null);
    }
}
