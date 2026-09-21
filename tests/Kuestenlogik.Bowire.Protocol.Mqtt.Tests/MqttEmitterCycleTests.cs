// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Mqtt.Mock;

namespace Kuestenlogik.Bowire.Protocol.Mqtt.Tests;

/// <summary>
/// How long one loop cycle of the proactive emitter lasts (#708).
/// </summary>
/// <remarks>
/// <para>
/// The emitter paced frames <em>inside</em> a run and nothing at all between
/// two runs, so <c>--replay-speed 0 --loop</c> published as fast as the CPU
/// allowed, without end. Both switches are documented on their own and both
/// are sensible; their product was described nowhere.
/// </para>
/// <para>
/// The arithmetic is asserted here rather than through a running broker
/// because that is where the decision lives. The behaviour — that a looped
/// mock cannot flood — is asserted against a real broker in
/// <c>MqttEmissionTests</c>, which is the only place it can be observed.
/// </para>
/// </remarks>
public sealed class MqttEmitterCycleTests
{
    private static long Floor => (long)MqttProactiveEmitter.MinimumCycle.TotalMilliseconds;

    [Fact]
    public void At_The_Original_Cadence_A_Cycle_Lasts_As_Long_As_The_Recording()
    {
        // Speed 1.0 is the case the change must not touch: the run already
        // takes the recording's span, so the wait at the end is for nothing.
        Assert.Equal(30_000, MqttProactiveEmitter.CycleLengthMs(30_000, 1.0));
    }

    [Fact]
    public void A_Faster_Replay_Gets_A_Shorter_Cycle()
    {
        // Otherwise --replay-speed 2 would be twice as fast inside the run
        // and unchanged end to end, which is not what the operator asked for.
        Assert.Equal(15_000, MqttProactiveEmitter.CycleLengthMs(30_000, 2.0));
        Assert.Equal(60_000, MqttProactiveEmitter.CycleLengthMs(30_000, 0.5));
    }

    [Fact]
    public void Speed_Zero_Still_Leaves_The_Recording_Worth_Its_Own_Span()
    {
        // The defect. "Emit every frame immediately" says something about
        // the frames, not about how often the recording repeats.
        Assert.Equal(30_000, MqttProactiveEmitter.CycleLengthMs(30_000, 0));
    }

    [Theory]
    [InlineData(0)]      // one step, or every step at the same instant
    [InlineData(1)]      // the two-step capture 1 ms apart
    [InlineData(999)]
    public void A_Recording_Shorter_Than_The_Floor_Is_Held_At_The_Floor(long spanMs)
    {
        // Without this, a 1 ms capture loops a thousand times a second — at
        // every speed, the default included. The floor is what makes "an
        // unbounded rate has to be asked for" true for short recordings too.
        Assert.Equal(Floor, MqttProactiveEmitter.CycleLengthMs(spanMs, 0));
        Assert.Equal(Floor, MqttProactiveEmitter.CycleLengthMs(spanMs, 1.0));
        Assert.Equal(Floor, MqttProactiveEmitter.CycleLengthMs(spanMs, 100.0));
    }

    [Fact]
    public void The_Floor_Never_Stretches_A_Recording_That_Is_Longer()
    {
        // A one-second floor must not become a one-second cycle for a
        // recording that covers a minute.
        Assert.Equal(60_000, MqttProactiveEmitter.CycleLengthMs(60_000, 1.0));
        Assert.True(MqttProactiveEmitter.CycleLengthMs(60_000, 1.0) > Floor);
    }

    // ---- the explicit interval, for somebody who wants the rate ----------
    //
    // The default refuses an unbounded rate; this is how it is asked for.
    // Which is the point of the whole change: a flood should be a decision,
    // not the product of two switches that each say something else.

    [Fact]
    public void An_Explicit_Interval_Is_Used_As_Given()
    {
        Assert.Equal(250, MqttProactiveEmitter.CycleLengthMs(30_000, 1.0, TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public void An_Explicit_Interval_Beats_The_Recording_In_Both_Directions()
    {
        // Shorter than the recording: the frames still pace the run, so the
        // cycle simply does not wait at the end.
        Assert.Equal(500, MqttProactiveEmitter.CycleLengthMs(30_000, 1.0, TimeSpan.FromMilliseconds(500)));
        // Longer: a one-second recording repeated every ten seconds.
        Assert.Equal(10_000, MqttProactiveEmitter.CycleLengthMs(1_000, 1.0, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void An_Explicit_Interval_Is_Not_Divided_By_The_Speed()
    {
        // The speed paces frames inside the run. An interval the operator
        // typed is an answer about cycles, and halving it behind their back
        // would make --loop-interval-ms mean something else per speed.
        Assert.Equal(1_000, MqttProactiveEmitter.CycleLengthMs(30_000, 2.0, TimeSpan.FromSeconds(1)));
        Assert.Equal(1_000, MqttProactiveEmitter.CycleLengthMs(30_000, 0, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Zero_Asks_For_No_Pacing_And_Gets_It()
    {
        // The floor does not apply here. It exists so nobody falls into an
        // unbounded rate; somebody who typed 0 has not fallen into anything.
        Assert.Equal(0, MqttProactiveEmitter.CycleLengthMs(30_000, 1.0, TimeSpan.Zero));
        Assert.Equal(0, MqttProactiveEmitter.CycleLengthMs(1, 0, TimeSpan.Zero));
        Assert.True(MqttProactiveEmitter.CycleLengthMs(1, 0, TimeSpan.Zero) < Floor);
    }

    [Fact]
    public void A_Negative_Interval_Is_Zero_Rather_Than_A_Negative_Wait()
    {
        // The CLI rejects a negative --loop-interval-ms; the API cannot, and
        // a negative wait is no wait, so it lands where it reads.
        Assert.Equal(0, MqttProactiveEmitter.CycleLengthMs(30_000, 1.0, TimeSpan.FromMilliseconds(-5)));
    }

    [Fact]
    public void Sub_Millisecond_Intervals_Round_Down_To_No_Pacing()
    {
        // TimeSpan carries ticks; the wait is in whole milliseconds. Anything
        // under one is nothing, and pretending otherwise would be a busy loop
        // that claims to be paced.
        Assert.Equal(0, MqttProactiveEmitter.CycleLengthMs(30_000, 1.0, TimeSpan.FromTicks(5_000)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void A_Negative_Span_Cannot_Produce_A_Negative_Cycle(long spanMs)
    {
        // Offsets are built from captured timestamps, which a hand-edited
        // recording can put out of order. A negative wait would be a flood
        // by another route.
        Assert.Equal(Floor, MqttProactiveEmitter.CycleLengthMs(spanMs, 1.0));
    }

    [Fact]
    public void A_Negative_Speed_Is_Treated_As_Zero_Like_The_Rest_Of_Replay()
    {
        // MockEmitterOptions: "Non-positive values other than 0 are treated
        // as 0." Dividing by a negative here would hand Task.Delay a
        // negative wait, i.e. no wait at all.
        Assert.Equal(30_000, MqttProactiveEmitter.CycleLengthMs(30_000, -2.0));
    }
}
