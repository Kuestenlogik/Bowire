// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mock.Replay;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// The rule that holds a streaming replay to the cadence its frames were
/// captured at.
/// </summary>
/// <remarks>
/// <para>
/// It was written out nine times in <c>UnaryReplayer</c>, once per streaming
/// path, and seven of the nine were reached by no test — the replay suites
/// run at speed 0, where there is nothing to wait for. Tested here once,
/// where the arithmetic is visible, rather than nine times through a socket.
/// </para>
/// <para>
/// Asserted on the interval the pacer <em>asks</em> for, never on a clock.
/// The first version of these tests timed the waits with a stopwatch and
/// one of them failed under load — which is the same mistake this file's
/// subject is about, and not one to make twice in the same afternoon.
/// </para>
/// </remarks>
public sealed class FramePacerTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A pacer whose waits are recorded rather than served.</summary>
    private sealed class Recorder
    {
        public List<double> WaitedMs { get; } = [];

        public FramePacer Pacer(double speed) => new(speed, (span, _) =>
        {
            WaitedMs.Add(span.TotalMilliseconds);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task At_Speed_Zero_Nothing_Waits()
    {
        // What --replay-speed 0 asks for, and what every replay suite runs
        // at: emit every frame immediately.
        var recorder = new Recorder();
        var pacer = recorder.Pacer(0);

        Assert.True(await pacer.WaitForAsync(5_000, None));

        Assert.Empty(recorder.WaitedMs);
    }

    [Fact]
    public async Task A_Frame_Without_A_Timestamp_Does_Not_Wait()
    {
        // Older recordings, and paths that never captured offsets. Their
        // frames still have to go out.
        var recorder = new Recorder();

        Assert.True(await recorder.Pacer(1).WaitForAsync(null, None));

        Assert.Empty(recorder.WaitedMs);
    }

    [Fact]
    public async Task The_First_Gap_Is_Measured_From_Zero()
    {
        var recorder = new Recorder();

        await recorder.Pacer(1).WaitForAsync(120, None);

        Assert.Equal([120], recorder.WaitedMs);
    }

    [Fact]
    public async Task Later_Gaps_Are_Measured_From_The_Frame_Before()
    {
        // The property that makes a replay look like the recording: each
        // wait is the gap to the previous frame, not the offset from the
        // start. Measured from zero every time, ten frames a second apart
        // would take fifty-five seconds instead of ten.
        var recorder = new Recorder();
        var pacer = recorder.Pacer(1);

        await pacer.WaitForAsync(100, None);
        await pacer.WaitForAsync(250, None);
        await pacer.WaitForAsync(400, None);

        Assert.Equal([100, 150, 150], recorder.WaitedMs);
    }

    [Fact]
    public async Task Speed_Divides_The_Gap()
    {
        // "2.0 is twice as fast", per the documented meaning of ReplaySpeed.
        var recorder = new Recorder();

        await recorder.Pacer(2).WaitForAsync(400, None);

        Assert.Equal([200], recorder.WaitedMs);
    }

    [Fact]
    public async Task A_Gap_Too_Small_To_Wait_For_Is_Not_Waited_For()
    {
        // Sub-millisecond after the division. Handing Task.Delay a zero is
        // a context switch bought for nothing, once per frame.
        var recorder = new Recorder();

        await recorder.Pacer(1000).WaitForAsync(10, None);

        Assert.Empty(recorder.WaitedMs);
    }

    [Fact]
    public async Task A_Frame_Out_Of_Order_Does_Not_Move_The_Origin()
    {
        // A merged send/receive timeline interleaves two clocks, so frames
        // are not guaranteed monotonic. Letting one backwards frame move the
        // origin would silently drop the pacing of everything after it.
        var recorder = new Recorder();
        var pacer = recorder.Pacer(1);

        await pacer.WaitForAsync(200, None);
        Assert.True(await pacer.WaitForAsync(150, None));
        await pacer.WaitForAsync(320, None);

        // 200, then nothing for the backwards frame, then 320-200.
        Assert.Equal([200, 120], recorder.WaitedMs);
    }

    [Fact]
    public async Task A_Cancelled_Wait_Tells_The_Caller_To_Stop()
    {
        // Each replay path does something different about it — break, or
        // return 200, or write a gRPC status trailer first — so the pacer
        // reports rather than decides.
        var pacer = new FramePacer(1, (_, ct) => Task.FromCanceled(
            new CancellationToken(canceled: true)));

        Assert.False(await pacer.WaitForAsync(5_000, None));
    }

    [Fact]
    public async Task A_Cancelled_Wait_Does_Not_Move_The_Origin()
    {
        // It never got there, so the next frame's gap is still measured from
        // where the replay actually was.
        var waited = new List<double>();
        var cancelFirst = true;
        var pacer = new FramePacer(1, (span, _) =>
        {
            if (cancelFirst)
            {
                cancelFirst = false;
                return Task.FromCanceled(new CancellationToken(canceled: true));
            }
            waited.Add(span.TotalMilliseconds);
            return Task.CompletedTask;
        });

        await pacer.WaitForAsync(5_000, None);
        await pacer.WaitForAsync(120, None);

        Assert.Equal([120], waited);
    }

    [Fact]
    public async Task An_Opened_Gate_Moves_The_Origin_To_Now()
    {
        // Input-gated replay: the next frame's gap is measured from when the
        // client let it through, not from the replay start — otherwise every
        // remaining frame bunches up behind a slow client.
        var recorder = new Recorder();
        var pacer = recorder.Pacer(1);

        pacer.Advance(1_000);
        await pacer.WaitForAsync(1_100, None);

        Assert.Equal([100], recorder.WaitedMs);
    }

    [Fact]
    public async Task A_Gate_Never_Moves_The_Origin_Backwards()
    {
        var recorder = new Recorder();
        var pacer = recorder.Pacer(1);

        pacer.Advance(1_000);
        pacer.Advance(100);
        await pacer.WaitForAsync(1_100, None);

        Assert.Equal([100], recorder.WaitedMs);
    }
}
