// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Recording;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit coverage for <see cref="BowireRecordingSession"/> (#285). Exercises
/// the state machine (idle → active → idle), the
/// <see cref="BowireRecordingSession.Subscribe"/> fan-out, and rejects
/// invalid transitions so callers see crisp <see cref="InvalidOperationException"/>s
/// instead of inconsistent state.
/// </summary>
public sealed class BowireRecordingSessionTests
{
    [Fact]
    public void Active_Returns_Null_When_Idle()
    {
        var session = new BowireRecordingSession();
        Assert.Null(session.Active);
    }

    [Fact]
    public void Start_Creates_New_Session_With_Provided_Metadata()
    {
        var session = new BowireRecordingSession();

        var state = session.Start(
            workspaceId: "ws-1",
            mode: BowireRecordingMode.Capture,
            name: "scenario A");

        Assert.NotNull(state);
        Assert.Equal("ws-1", state.WorkspaceId);
        Assert.Equal(BowireRecordingMode.Capture, state.Mode);
        Assert.Equal("scenario A", state.Name);
        Assert.StartsWith("rec_", state.RecordingId, StringComparison.Ordinal);
        Assert.Equal(0, state.StepCount);

        // Active is a defensive snapshot — equal-by-value but not the
        // same reference as the one Start returned.
        Assert.NotNull(session.Active);
        Assert.Equal(state.RecordingId, session.Active!.RecordingId);
    }

    [Fact]
    public void Start_Uses_Default_Name_When_Empty()
    {
        var session = new BowireRecordingSession();
        var state = session.Start("ws", BowireRecordingMode.Capture);
        Assert.Equal("Untitled recording", state.Name);
    }

    [Fact]
    public void Start_Accepts_Caller_Supplied_RecordingId()
    {
        var session = new BowireRecordingSession();
        var state = session.Start("ws", BowireRecordingMode.Replay,
            recordingId: "rec_predetermined");
        Assert.Equal("rec_predetermined", state.RecordingId);
    }

    [Fact]
    public void Start_Throws_When_Session_Already_Active()
    {
        var session = new BowireRecordingSession();
        session.Start("ws", BowireRecordingMode.Capture);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            session.Start("ws", BowireRecordingMode.Capture));

        Assert.Contains("already active", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendStep_Throws_When_Idle()
    {
        var session = new BowireRecordingSession();
        var step = new BowireRecordingStep { Id = "step1", Service = "S", Method = "M" };
        var ex = Assert.Throws<InvalidOperationException>(() => session.AppendStep(step));
        Assert.Contains("No active recording session", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendStep_Buffers_Step_And_Returns_Index()
    {
        var session = new BowireRecordingSession();
        session.Start("ws", BowireRecordingMode.Capture);

        var i0 = session.AppendStep(new BowireRecordingStep { Id = "s1" });
        var i1 = session.AppendStep(new BowireRecordingStep { Id = "s2" });

        Assert.Equal(0, i0);
        Assert.Equal(1, i1);
        Assert.Equal(2, session.Active!.StepCount);
    }

    [Fact]
    public void SwitchToReplay_Flips_Mode_Without_Dropping_Buffer()
    {
        var session = new BowireRecordingSession();
        session.Start("ws", BowireRecordingMode.Capture);
        session.AppendStep(new BowireRecordingStep { Id = "s1" });

        var state = session.SwitchToReplay();

        Assert.Equal(BowireRecordingMode.Replay, state.Mode);
        Assert.Equal(1, state.StepCount); // buffer preserved
    }

    [Fact]
    public void SwitchToReplay_Throws_When_Idle()
    {
        var session = new BowireRecordingSession();
        Assert.Throws<InvalidOperationException>(() => session.SwitchToReplay());
    }

    [Fact]
    public void Stop_Returns_Null_When_Idle()
    {
        var session = new BowireRecordingSession();
        Assert.Null(session.Stop());
    }

    [Fact]
    public void Stop_Flushes_Buffer_Into_BowireRecording()
    {
        var session = new BowireRecordingSession();
        session.Start("ws", BowireRecordingMode.Capture, name: "scenario");
        session.AppendStep(new BowireRecordingStep { Id = "s1", Service = "Foo", Method = "Bar" });
        session.AppendStep(new BowireRecordingStep { Id = "s2", Service = "Foo", Method = "Baz" });

        var recording = session.Stop();

        Assert.NotNull(recording);
        Assert.Equal("scenario", recording!.Name);
        Assert.Equal(2, recording.Steps.Count);
        Assert.Equal("s1", recording.Steps[0].Id);
        Assert.Equal("s2", recording.Steps[1].Id);
        Assert.Null(session.Active); // session cleared
    }

    [Fact]
    public void Stop_Invokes_Flush_Sink_With_The_Persisted_Recording()
    {
        var session = new BowireRecordingSession();
        session.Start("ws", BowireRecordingMode.Capture);

        BowireRecording? seenByFlush = null;
        var result = session.Stop(rec =>
        {
            seenByFlush = rec;
            rec.Name = "flush-renamed";
            return rec;
        });

        Assert.NotNull(seenByFlush);
        Assert.Same(seenByFlush, result);
        Assert.Equal("flush-renamed", result!.Name);
    }

    [Fact]
    public void Subscribe_Receives_Started_StepAppended_Stopped_Events_In_Order()
    {
        var session = new BowireRecordingSession();
        var (reader, sub) = session.Subscribe();
        using var _ = sub;

        session.Start("ws", BowireRecordingMode.Capture, name: "scenario");
        session.AppendStep(new BowireRecordingStep { Id = "s1" });
        session.Stop();

        // Drain the channel — three events should be queued.
        var events = new List<BowireRecordingSessionEvent>();
        while (reader.TryRead(out var evt)) events.Add(evt);

        Assert.Equal(3, events.Count);
        Assert.Equal(BowireRecordingSessionEventKind.Started, events[0].Kind);
        Assert.Equal(BowireRecordingSessionEventKind.StepAppended, events[1].Kind);
        Assert.Equal(0, events[1].StepIndex);
        Assert.Equal(BowireRecordingSessionEventKind.Stopped, events[2].Kind);
        Assert.NotNull(events[2].Recording);
    }

    [Fact]
    public void Subscribe_Unsubscribe_Stops_Receiving_Events()
    {
        var session = new BowireRecordingSession();
        var (reader, sub) = session.Subscribe();

        session.Start("ws", BowireRecordingMode.Capture);

        // Drain the Started event then unsubscribe.
        Assert.True(reader.TryRead(out _));
        sub.Dispose();

        // Append shouldn't reach our channel anymore.
        session.AppendStep(new BowireRecordingStep { Id = "s1" });

        // Reader is completed; ReadAllAsync would terminate. TryRead
        // returns false on an empty completed channel.
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void Two_Subscribers_Each_Receive_All_Events_Independently()
    {
        var session = new BowireRecordingSession();
        var (r1, sub1) = session.Subscribe();
        var (r2, sub2) = session.Subscribe();
        using var _1 = sub1;
        using var _2 = sub2;

        session.Start("ws", BowireRecordingMode.Capture);
        session.AppendStep(new BowireRecordingStep { Id = "s1" });

        var c1 = 0;
        while (r1.TryRead(out _)) c1++;
        var c2 = 0;
        while (r2.TryRead(out _)) c2++;

        Assert.Equal(2, c1);
        Assert.Equal(2, c2);
    }

    [Fact]
    public void Snapshot_Buffer_Is_Read_Only_View()
    {
        var session = new BowireRecordingSession();
        session.Start("ws", BowireRecordingMode.Capture);
        session.AppendStep(new BowireRecordingStep { Id = "s1" });

        var snap = session.Active!;
        Assert.Single(snap.SnapshotBuffer);

        // Snapshot's buffer is a defensive copy — mutating the active
        // session shouldn't affect the snapshot we already pulled.
        session.AppendStep(new BowireRecordingStep { Id = "s2" });
        Assert.Single(snap.SnapshotBuffer);
        Assert.Equal(2, session.Active!.SnapshotBuffer.Count);
    }
}
