// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Recording;

/// <summary>
/// Server-side active-recording lifecycle (#285). Lives in core as a
/// singleton so multiple consumers — the workbench HTTP surface, the
/// MCP tool surface (<c>bowire.record.start / stop / replay</c>), and
/// the in-process invoke / channel pipelines that need to know "is there
/// a recording open right now?" — share one source of truth instead of
/// the pre-#285 browser-localStorage-as-truth pattern.
/// </summary>
/// <remarks>
/// <para>
/// Pre-#285 the active-recording state lived in the browser
/// (<c>bowire_recording_active</c>, <c>bowire_recording_buffer</c>); the
/// MCP server had no way to reach it, so the
/// <c>bowire.record.start / stop / replay</c> tools shipped as deferred
/// no-ops. Lifting the state into this singleton unblocks the tools
/// without forcing the workbench to give up its localStorage cache for
/// completed recordings (the <c>bowire_recordings</c> bucket stays — it's
/// a flush sink, not the source of truth).
/// </para>
/// <para>
/// State machine: <c>Idle → Active (Capture | Proxy | Replay) → Idle</c>.
/// <see cref="Start"/> transitions Idle → Active and rejects if a session
/// is already open (callers must <see cref="Stop"/> first). <see cref="Stop"/>
/// flushes the buffer to the supplied sink and returns the persisted
/// recording. <see cref="SwitchToReplay"/> flips the mode in-place without
/// dropping the buffer.
/// </para>
/// <para>
/// Subscribers (<see cref="Subscribe"/>) receive a
/// <see cref="ChannelReader{T}"/> of <see cref="BowireRecordingSessionEvent"/>;
/// the SSE endpoint translates each event to a <c>text/event-stream</c>
/// frame for the workbench's recorder UI. The channel is bounded with
/// drop-oldest semantics — a wedged subscriber can't grow memory
/// unbounded, and the workbench is interested in "latest state" not "every
/// transition since it last polled".
/// </para>
/// </remarks>
public sealed class BowireRecordingSession
{
    /// <summary>
    /// Bounded subscriber channel cap. 64 transitions is enough headroom
    /// for the slowest browser tab to drain while a CLI agent rapidly
    /// starts / stops; DropOldest matches "the UI cares about the latest
    /// session state", not "the UI needs every historical transition".
    /// </summary>
    public const int SubscriberChannelCapacity = 64;

    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<Guid, ChannelWriter<BowireRecordingSessionEvent>> _subscribers = new();
    private BowireRecordingSessionState? _active;

    /// <summary>
    /// Currently-active session, or <c>null</c> when the recorder is idle.
    /// Returns an immutable snapshot — callers can inspect without holding
    /// the gate. Mutating tools (<see cref="AppendStep"/>,
    /// <see cref="Stop"/>) go through the dedicated methods so the gate
    /// is held for the transition.
    /// </summary>
    public BowireRecordingSessionState? Active
    {
        get { lock (_gate) return _active is null ? null : _active.Snapshot(); }
    }

    /// <summary>
    /// Start a new recording session. Returns the freshly-created
    /// snapshot. Throws when a session is already open — callers must
    /// <see cref="Stop"/> or <see cref="SwitchToReplay"/> first.
    /// </summary>
    /// <param name="workspaceId">
    /// Workspace the recording lives under. Empty string is accepted (legacy
    /// unscoped workspaces), but the caller should prefer the active
    /// workspace id so multi-workspace operators don't see cross-leakage.
    /// </param>
    /// <param name="mode">
    /// One of <see cref="BowireRecordingMode.Capture"/> (record live
    /// invocations), <see cref="BowireRecordingMode.Proxy"/> (record proxied
    /// flows), <see cref="BowireRecordingMode.Replay"/> (drive replay from
    /// a pre-existing recording).
    /// </param>
    /// <param name="name">
    /// Optional human-readable name. Defaults to <c>"Untitled recording"</c>
    /// so the UI always has something to show.
    /// </param>
    /// <param name="recordingId">
    /// Optional caller-supplied id (used by replay to bind to an existing
    /// recording). When null, a fresh <c>rec_*</c> id is generated.
    /// </param>
    public BowireRecordingSessionState Start(
        string workspaceId,
        BowireRecordingMode mode,
        string? name = null,
        string? recordingId = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceId);
        BowireRecordingSessionState snapshot;
        lock (_gate)
        {
            if (_active is not null)
            {
                throw new InvalidOperationException(
                    $"A recording session is already active (id={_active.RecordingId}, mode={_active.Mode}). Stop it before starting a new one.");
            }

            _active = new BowireRecordingSessionState
            {
                RecordingId = recordingId ?? NewRecordingId(),
                WorkspaceId = workspaceId,
                StartedAt = DateTimeOffset.UtcNow,
                Mode = mode,
                Name = string.IsNullOrWhiteSpace(name) ? "Untitled recording" : name!,
                Buffer = new List<BowireRecordingStep>(),
            };
            snapshot = _active.Snapshot();
        }
        Broadcast(BowireRecordingSessionEvent.Started(snapshot));
        return snapshot;
    }

    /// <summary>
    /// Append a captured step to the active session's buffer. Returns the
    /// step's 0-based index. Throws when no session is active.
    /// </summary>
    public int AppendStep(BowireRecordingStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        int index;
        BowireRecordingSessionState snapshot;
        lock (_gate)
        {
            if (_active is null)
                throw new InvalidOperationException(
                    "No active recording session to append to. Call Start() first.");
            _active.Buffer.Add(step);
            index = _active.Buffer.Count - 1;
            snapshot = _active.Snapshot();
        }
        Broadcast(BowireRecordingSessionEvent.StepAppended(snapshot, index));
        return index;
    }

    /// <summary>
    /// Flip the active session into <see cref="BowireRecordingMode.Replay"/>.
    /// Buffer is preserved (replay reads from it). Throws when no session
    /// is active.
    /// </summary>
    public BowireRecordingSessionState SwitchToReplay()
    {
        BowireRecordingSessionState snapshot;
        lock (_gate)
        {
            if (_active is null)
                throw new InvalidOperationException(
                    "No active recording session to switch into replay. Call Start(mode: Replay) instead.");
            _active.Mode = BowireRecordingMode.Replay;
            snapshot = _active.Snapshot();
        }
        Broadcast(BowireRecordingSessionEvent.ModeSwitched(snapshot));
        return snapshot;
    }

    /// <summary>
    /// Stop the active session and hand the assembled recording to the
    /// supplied flush sink. The sink's return value is propagated back so
    /// the caller (REST endpoint, MCP tool) can surface the persisted
    /// recording id. When <paramref name="flush"/> is null the recording
    /// is returned without being persisted — useful for replay sessions
    /// that don't want a new entry in the recording store.
    /// </summary>
    /// <returns>
    /// The final <see cref="BowireRecording"/> the session produced.
    /// <c>null</c> only if no session was active in the first place
    /// (idempotent stop).
    /// </returns>
    public BowireRecording? Stop(Func<BowireRecording, BowireRecording>? flush = null)
    {
        BowireRecordingSessionState? closed;
        BowireRecording? recording;
        lock (_gate)
        {
            if (_active is null) return null;
            closed = _active;
            _active = null;

            recording = new BowireRecording
            {
                Id = closed.RecordingId,
                Name = closed.Name,
                CreatedAt = closed.StartedAt.ToUnixTimeMilliseconds(),
            };
            foreach (var step in closed.Buffer)
            {
                recording.Steps.Add(step);
            }
        }

        if (flush is not null)
        {
            recording = flush(recording);
        }
        Broadcast(BowireRecordingSessionEvent.Stopped(closed.Snapshot(), recording));
        return recording;
    }

    /// <summary>
    /// Subscribe to session-state-change events. Returns the channel reader
    /// the SSE producer streams from and an <see cref="IDisposable"/> that,
    /// when disposed, unsubscribes. Each subscriber gets a private bounded
    /// channel so a slow browser tab can't back-pressure other subscribers
    /// or the producer.
    /// </summary>
    public (ChannelReader<BowireRecordingSessionEvent> Reader, IDisposable Subscription) Subscribe()
    {
        var channel = Channel.CreateBounded<BowireRecordingSessionEvent>(
            new BoundedChannelOptions(SubscriberChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        var id = Guid.NewGuid();
        _subscribers[id] = channel.Writer;
        return (channel.Reader, new Unsubscriber(this, id, channel));
    }

    private void Broadcast(BowireRecordingSessionEvent evt)
    {
        foreach (var (_, writer) in _subscribers)
        {
            // TryWrite respects the DropOldest policy — a wedged subscriber
            // just loses the oldest queued transition, never blocks the
            // producer.
            writer.TryWrite(evt);
        }
    }

    private static string NewRecordingId() =>
        "rec_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x",
            System.Globalization.CultureInfo.InvariantCulture)
            + "_" + Guid.NewGuid().ToString("N")[..6];

    private sealed class Unsubscriber : IDisposable
    {
        private readonly BowireRecordingSession _owner;
        private readonly Guid _id;
        private readonly Channel<BowireRecordingSessionEvent> _channel;
        private int _disposed;

        public Unsubscriber(BowireRecordingSession owner, Guid id, Channel<BowireRecordingSessionEvent> channel)
        {
            _owner = owner;
            _id = id;
            _channel = channel;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_owner._subscribers.TryRemove(_id, out var writer))
            {
                writer.TryComplete();
            }
            _ = _channel;
        }
    }
}

/// <summary>
/// Mode the active recording session is running in. Drives the workbench
/// UI badge (red dot for capture, blue dot for replay) and the
/// invoke / channel pipelines' decision to push frames into the
/// <see cref="BowireRecordingSession"/> buffer.
/// </summary>
public enum BowireRecordingMode
{
    /// <summary>
    /// Capture live invocations driven from the workbench UI or MCP tools.
    /// Each invoke / channel call appends a step to the buffer.
    /// </summary>
    Capture = 0,

    /// <summary>
    /// Capture flows tunnelled through the Bowire proxy server. Same
    /// append-on-call semantics as <see cref="Capture"/>; differs in that
    /// the trigger is intercepted-proxy traffic rather than direct
    /// workbench invocations.
    /// </summary>
    Proxy = 1,

    /// <summary>
    /// Drive replay from a pre-existing recording. The buffer is read-only
    /// from the session's perspective — the replay pipeline pulls steps
    /// out of it; nothing appends to it.
    /// </summary>
    Replay = 2,
}

/// <summary>
/// Immutable snapshot of the active recording session. Returned by
/// <see cref="BowireRecordingSession.Active"/>, broadcast on
/// <see cref="BowireRecordingSessionEvent"/>s. Mutable bookkeeping
/// (the <see cref="Buffer"/> list) is held internally; this snapshot
/// exposes a read-only view.
/// </summary>
public sealed class BowireRecordingSessionState
{
    public required string RecordingId { get; init; }
    public required string WorkspaceId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public BowireRecordingMode Mode { get; set; }
    public required string Name { get; init; }

    /// <summary>
    /// Internal mutable buffer. Externally exposed through
    /// <see cref="SnapshotBuffer"/> as a read-only view.
    /// </summary>
    internal List<BowireRecordingStep> Buffer { get; init; } = new();

    /// <summary>Read-only view of the captured steps.</summary>
    public IReadOnlyList<BowireRecordingStep> SnapshotBuffer => Buffer;

    /// <summary>Count of steps captured so far.</summary>
    public int StepCount => Buffer.Count;

    /// <summary>
    /// Take a defensive copy so the caller can't mutate the buffer
    /// out from under the session. Used by every public getter / event.
    /// </summary>
    internal BowireRecordingSessionState Snapshot()
    {
        var copy = new BowireRecordingSessionState
        {
            RecordingId = RecordingId,
            WorkspaceId = WorkspaceId,
            StartedAt = StartedAt,
            Mode = Mode,
            Name = Name,
        };
        // Buffer is a defensive copy — but only the list ref, not the
        // step objects. Steps are append-only so caller mutation is
        // not a real concern; we just don't want a caller to add to the
        // live buffer through the snapshot.
        foreach (var step in Buffer) copy.Buffer.Add(step);
        return copy;
    }
}

/// <summary>
/// One transition broadcast on <see cref="BowireRecordingSession.Subscribe"/>.
/// The SSE producer maps <see cref="Kind"/> to the SSE <c>event:</c> name
/// and serialises the payload as the <c>data:</c> JSON body.
/// </summary>
public sealed record BowireRecordingSessionEvent
{
    public required BowireRecordingSessionEventKind Kind { get; init; }

    /// <summary>
    /// Session snapshot at the moment of the event. <c>null</c> only for
    /// <see cref="BowireRecordingSessionEventKind.Stopped"/> after the
    /// state has been cleared — and even then we surface the just-closed
    /// state so subscribers know what flushed.
    /// </summary>
    public BowireRecordingSessionState? Session { get; init; }

    /// <summary>
    /// On <see cref="BowireRecordingSessionEventKind.StepAppended"/> — the
    /// 0-based index of the freshly-appended step.
    /// </summary>
    public int? StepIndex { get; init; }

    /// <summary>
    /// On <see cref="BowireRecordingSessionEventKind.Stopped"/> — the
    /// persisted recording.
    /// </summary>
    public BowireRecording? Recording { get; init; }

    public static BowireRecordingSessionEvent Started(BowireRecordingSessionState session) =>
        new() { Kind = BowireRecordingSessionEventKind.Started, Session = session };

    public static BowireRecordingSessionEvent StepAppended(BowireRecordingSessionState session, int index) =>
        new() { Kind = BowireRecordingSessionEventKind.StepAppended, Session = session, StepIndex = index };

    public static BowireRecordingSessionEvent ModeSwitched(BowireRecordingSessionState session) =>
        new() { Kind = BowireRecordingSessionEventKind.ModeSwitched, Session = session };

    public static BowireRecordingSessionEvent Stopped(BowireRecordingSessionState session, BowireRecording? recording) =>
        new() { Kind = BowireRecordingSessionEventKind.Stopped, Session = session, Recording = recording };
}

/// <summary>SSE event-name discriminator.</summary>
public enum BowireRecordingSessionEventKind
{
    Started,
    StepAppended,
    ModeSwitched,
    Stopped,
}
