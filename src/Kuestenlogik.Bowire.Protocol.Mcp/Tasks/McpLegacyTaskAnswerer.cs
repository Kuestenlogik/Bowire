// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tasks;

/// <summary>
/// Opens a new client connection for one followed 2025-11-25 task, for the
/// server's questions (#821).
/// The spec has the requestor call <c>tasks/result</c> once a task turns
/// <c>input_required</c>; the server then sends <c>elicitation/create</c>,
/// tagged with the task, on that request. This opens a connection that
/// declares <c>elicitation</c> — only this one, so every other call keeps
/// the capabilities it has — calls <c>tasks/result</c> on it, and puts each
/// question into <see cref="McpLegacyTaskInputs"/>, where
/// <c>Tasks/update</c> answers it.
/// </summary>
/// <remarks>
/// It also owns the task's status-notification handler, so one dispose
/// ends the questions first and the notifications after.
/// The answer to <c>tasks/result</c> itself is not used: the follower reads
/// the outcome on its own session once a poll shows the task ended, as it
/// does for every task.
/// </remarks>
internal sealed class McpLegacyTaskAnswerer : IAsyncDisposable
{
    /// <summary>The <c>_meta</c> key a 2025-11-25 server tags a task's requests with.</summary>
    internal const string RelatedTaskMeta = "io.modelcontextprotocol/related-task";

    private readonly McpLegacyTaskInputs _inputs;
    private readonly string _origin;
    private readonly string _taskId;
    private readonly Func<McpClientOptions, CancellationToken, Task<McpClient>> _connect;
    private readonly Action<McpTaskState> _onStatus;
    private readonly IAsyncDisposable _notifications;
    private readonly CancellationTokenSource _stop;
    private readonly Lock _gate = new();
    private McpTaskState? _last;
    private Task? _running;
    private bool _unreachable;
    private bool _answered;

    public McpLegacyTaskAnswerer(
        McpLegacyTaskInputs inputs, string origin, string taskId,
        Func<McpClientOptions, CancellationToken, Task<McpClient>> connect,
        Action<McpTaskState> onStatus, IAsyncDisposable notifications, CancellationToken ct)
    {
        _notifications = notifications;
        _inputs = inputs;
        _origin = origin;
        _taskId = taskId;
        _connect = connect;
        _onStatus = onStatus;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    /// <summary>
    /// The follower reports every state it adopts. An <c>input_required</c>
    /// one starts <c>tasks/result</c> on the answering connection, unless
    /// that call is already waiting.
    /// </summary>
    public void Observe(McpTaskState state)
    {
        lock (_gate)
        {
            _last = state;
            if (state.Status != McpTaskStatuses.InputRequired || _unreachable || _answered || _stop.IsCancellationRequested) return;
            if (_running is { IsCompleted: false }) return;
            _running = Task.Run(() => WaitForQuestionsAsync(_stop.Token));
        }
    }

    private async Task WaitForQuestionsAsync(CancellationToken ct)
    {
        McpClient answering;
        try
        {
            answering = await _connect(new McpClientOptions
            {
                Capabilities = new ClientCapabilities
                {
                    Elicitation = new ElicitationCapability { Form = new(), Url = new() },
                },
                Handlers = new McpClientHandlers { ElicitationHandler = ElicitAsync },
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or IOException or InvalidOperationException)
        {
            // Say it once and do not reconnect on every poll.
            McpTaskState? last;
            lock (_gate)
            {
                _unreachable = true;
                last = _last;
            }
            if (last is not null)
            {
                _onStatus(last with
                {
                    StatusMessage = $"Bowire could not open the connection that receives this task's input requests: {ex.Message}",
                });
            }
            return;
        }

        await using (answering)
        {
            try
            {
                await answering.SendRequestAsync(new JsonRpcRequest
                {
                    Method = "tasks/result",
                    Params = new JsonObject { ["taskId"] = _taskId },
                }, ct).ConfigureAwait(false);
                Answered();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* the follow ended */ }
            catch (McpProtocolException)
            {
                // A failed task answers tasks/result with its error, which
                // the follower reads on its own session. Nothing to add here.
                Answered();
            }
            catch (Exception ex) when (ex is McpException or HttpRequestException or IOException or JsonException)
            {
                // The connection broke while waiting: the next input_required
                // the follower sees opens another one.
            }
        }
    }

    /// <summary>
    /// tasks/result answered, so the task has ended — the server holds the
    /// call until it does. A poll read before that can still say
    /// <c>input_required</c>; it must not open another connection.
    /// </summary>
    private void Answered()
    {
        lock (_gate) _answered = true;
    }

    private async ValueTask<ElicitResult> ElicitAsync(ElicitRequestParams? request, CancellationToken ct)
    {
        // A question tagged with another task is not this follow's to ask.
        var related = request?.Meta?[RelatedTaskMeta]?["taskId"];
        if (related is JsonValue value && value.TryGetValue<string>(out var id) && id != _taskId)
            return new ElicitResult { Action = "decline" };

        var (key, answer) = _inputs.Add(_origin, _taskId, RequestMethods.ElicitationCreate,
            JsonSerializer.SerializeToNode(request, McpJsonUtilities.DefaultOptions));
        try
        {
            Show();
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            var response = await answer.WaitAsync(wait.Token).ConfigureAwait(false);
            return response.Deserialize<ElicitResult>(McpJsonUtilities.DefaultOptions)!;
        }
        finally
        {
            _inputs.Remove(_origin, _taskId, key);
        }
    }

    /// <summary>Put the waiting questions on the task frame now, rather than at the next poll.</summary>
    private void Show()
    {
        McpTaskState? last;
        lock (_gate) last = _last;
        if (last is not null) _onStatus(last with { InputRequests = _inputs.Snapshot(_origin, _taskId) });
    }

    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (_gate) running = _running;
        // Observe checks the token under the gate, so after this nothing new starts.
        await _stop.CancelAsync().ConfigureAwait(false);
        lock (_gate) running = _running;
        if (running is not null)
        {
            try { await running.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* stopping is what was asked */ }
        }
        _stop.Dispose();
        _inputs.Forget(_origin, _taskId);
        await _notifications.DisposeAsync().ConfigureAwait(false);
    }
}
