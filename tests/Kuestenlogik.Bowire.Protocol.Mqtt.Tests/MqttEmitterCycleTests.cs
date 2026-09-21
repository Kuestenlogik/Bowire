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
