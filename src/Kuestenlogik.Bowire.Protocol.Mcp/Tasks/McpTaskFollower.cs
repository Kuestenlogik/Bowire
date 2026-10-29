// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tasks;

/// <summary>One step of a followed task: a <c>task</c> state, its <c>result</c>, or an <c>error</c>.</summary>
internal readonly record struct McpTaskEvent(string Kind, JsonElement Data, McpTaskState? State = null);

/// <summary>
/// Follows one task to a terminal state (#801), in either shape: a
/// <c>task</c> event whenever the server reports something new, then the
/// result (<c>completed</c>), the JSON-RPC error (<c>failed</c>), or
/// nothing more (<c>cancelled</c> — the last task event already says so).
/// </summary>
/// <remarks>
/// <para>
/// Polling is the protocol here, not a workaround: a task server answers
/// <c>tasks/get</c> and names the interval it wants polls at
/// (<c>pollIntervalMs</c>), and Bowire honours it. Where the server agreed
/// to push status (the extension's <c>subscriptions/listen</c>), the
/// notifications are the signal and polling stops — until the subscription
/// stream ends, when it resumes.
/// </para>
/// <para>
/// The TTL is a backstop the spec offers the client: past
/// <c>createdAt + ttlMs</c> a task that has not reached a terminal state may
/// be considered gone, and the server may already have dropped it. Bowire
/// says so instead of polling a handle nobody will answer for.
/// </para>
/// </remarks>
internal static class McpTaskFollower
{
    /// <summary>Used when the server names no poll interval.</summary>
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    public static async IAsyncEnumerable<McpTaskEvent> FollowAsync(
        McpClient client, IMcpTaskDialect dialect, string taskId, McpTaskState? created,
        TimeProvider? clock = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        clock ??= TimeProvider.System;

        var (state, via, failure) = created is not null
            ? (created, "create", null)
            : await PollAsync(client, dialect, taskId, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            yield return failure.Value;
            yield break;
        }

        yield return TaskFrame(state!, via);

        if (!state!.IsTerminal)
        {
            var notes = Channel.CreateUnbounded<McpTaskState>(new UnboundedChannelOptions { SingleReader = true });
            await using var listener = await dialect.ListenAsync(client, taskId, s => notes.Writer.TryWrite(s), ct).ConfigureAwait(false);
            listener.Observe(state);
            await foreach (var step in WatchAsync(client, dialect, state, notes.Reader, listener, clock, ct).ConfigureAwait(false))
            {
                if (step.State is { } s) state = s;
                yield return step;
                if (step.Kind == "error") yield break;
            }
        }

        // A pushed `completed` carries its result inline in the extension;
        // the older shape's notification does not, nor does a create that
        // came back already terminal. Read it then.
        if (state.Status is McpTaskStatuses.Completed or McpTaskStatuses.Failed && state.Result is null && state.Error is null)
        {
            var (read, _, failure2) = await PollAsync(client, dialect, taskId, ct).ConfigureAwait(false);
            if (failure2 is not null)
            {
                yield return failure2.Value;
                yield break;
            }
            state = read!;
        }

        if (state.Result is { } result)
        {
            yield return new McpTaskEvent("result", result, state);
        }
        else if (state.Status == McpTaskStatuses.Failed)
        {
            yield return Failed(state);
        }
    }

    /// <summary>The task frames from the first observation until a terminal one, or an error.</summary>
    private static async IAsyncEnumerable<McpTaskEvent> WatchAsync(
        McpClient client, IMcpTaskDialect dialect, McpTaskState state,
        ChannelReader<McpTaskState> notes, McpTaskListener listener, TimeProvider clock,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var taskId = state.TaskId;
        while (!state.IsTerminal)
        {
            if (state.ExpiresAt is { } expires && clock.GetUtcNow() >= expires)
            {
                yield return Error(taskId,
                    $"Task '{taskId}' is past its TTL ({state.TtlMs} ms from {state.CreatedAt:O}) and still '{state.Status}'. "
                    + "The server may discard it at any time; Bowire stopped following it.");
                yield break;
            }

            var pushed = listener.Subscribed && !listener.Ended.IsCompleted;
            var next = await NextAsync(client, dialect, state, notes, listener, pushed, clock, ct).ConfigureAwait(false);
            if (next.Failure is not null)
            {
                yield return next.Failure.Value;
                yield break;
            }
            if (next.State is null) continue;

            var changed = !next.State.SameAs(state);
            state = next.State;
            listener.Observe(state);
            if (changed) yield return TaskFrame(state, next.Via);
        }
    }

    private readonly record struct Step(McpTaskState? State, string Via, McpTaskEvent? Failure);

    private static async Task<Step> NextAsync(
        McpClient client, IMcpTaskDialect dialect, McpTaskState state,
        ChannelReader<McpTaskState> notes, McpTaskListener listener, bool pushed,
        TimeProvider clock, CancellationToken ct)
    {
        if (notes.TryRead(out var note)) return new Step(note, "notification", null);

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var noteArrives = notes.WaitToReadAsync(wait.Token).AsTask();
        // Subscribed: nothing to poll for — wait for the push, for the
        // stream to end, or for the TTL backstop. Otherwise the server's
        // interval decides when to ask again.
        // #868 — never past the TTL backstop either way, so a long poll interval
        // cannot sleep through it. Every wait is capped at what Task.Delay
        // accepts (about 49.7 days): the server names the numbers, and the
        // loop re-decides after each wait, so a capped one loses nothing.
        var toExpiry = state.ExpiresAt is { } expires ? Max(expires - clock.GetUtcNow(), TimeSpan.Zero) : (TimeSpan?)null;
        var poll = state.PollIntervalMs is { } ms && ms > 0
            ? ms >= MaxWait.TotalMilliseconds ? MaxWait : TimeSpan.FromMilliseconds(ms)
            : DefaultPollInterval;
        var until = pushed
            ? toExpiry is { } left ? Min(left, MaxWait) : Timeout.InfiniteTimeSpan
            : toExpiry is { } remaining ? Min(poll, remaining) : poll;
        var timer = Task.Delay(until, clock, wait.Token);

        var first = pushed
            ? await Task.WhenAny(noteArrives, timer, listener.Ended).ConfigureAwait(false)
            : await Task.WhenAny(noteArrives, timer).ConfigureAwait(false);
        await wait.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (first == noteArrives && notes.TryRead(out note)) return new Step(note, "notification", null);
        if (pushed) return new Step(null, "", null); // TTL check or stream end — the loop re-decides

        var polled = await PollAsync(client, dialect, state.TaskId, ct).ConfigureAwait(false);
        return new Step(polled.State, polled.Via, polled.Failure);
    }

    private static async Task<(McpTaskState? State, string Via, McpTaskEvent? Failure)> PollAsync(
        McpClient client, IMcpTaskDialect dialect, string taskId, CancellationToken ct)
    {
        try
        {
            return (await dialect.GetAsync(client, taskId, ct).ConfigureAwait(false), "poll", null);
        }
        catch (McpProtocolException ex) when (ex.ErrorCode == McpErrorCode.InvalidParams)
        {
            // -32602 is the spec's answer for an unknown id — and for one
            // the server purged after its TTL. The two are indistinguishable
            // from here, so the message names both.
            return (null, "", Error(taskId,
                $"The server does not know task '{taskId}' (any more): it expired and was purged, or never existed. ({ex.Message})",
                (int)ex.ErrorCode));
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or JsonException)
        {
            return (null, "", Error(taskId, $"Reading task '{taskId}' failed: {ex.Message}"));
        }
    }

    /// <summary>The longest wait <see cref="Task.Delay(TimeSpan)"/> accepts.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static McpTaskEvent TaskFrame(McpTaskState state, string via) => new("task", state.ToFrameData(via), state);

    private static McpTaskEvent Failed(McpTaskState state)
    {
        var error = state.Error;
        string? message = null;
        int? code = null;
        if (error is { ValueKind: JsonValueKind.Object } e)
        {
            if (e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
            // #869 — a JSON-RPC code is an integer, but this one came from a server
            // that may not hold to that; a string code is shown in the
            // `error` object below rather than crashing the follow.
            if (e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n)) code = n;
        }
        return new McpTaskEvent("error", JsonSerializer.SerializeToElement(new
        {
            message = message ?? state.StatusMessage ?? $"Task '{state.TaskId}' failed.",
            code,
            taskId = state.TaskId,
            error,
        }), state);
    }

    internal static McpTaskEvent Error(string taskId, string message, int? code = null) =>
        new("error", JsonSerializer.SerializeToElement(new { message, code, taskId }));
}
