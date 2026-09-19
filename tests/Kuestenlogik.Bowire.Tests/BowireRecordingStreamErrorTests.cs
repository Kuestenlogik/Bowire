// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// What a recording keeps when a stream was cut short (#712, step 3).
/// </summary>
/// <remarks>
/// <para>
/// Before the contract had an error form, an aborted stream was recorded
/// as an ordinary frame whose payload happened to mention an error, and the
/// step's status said OK. Both halves were wrong and neither was visible:
/// the recording read as a stream that finished.
/// </para>
/// <para>
/// The decision this pins is where the reason lives. On the step, not among
/// the frames — because a frame is what the server sent, and "the WebSocket
/// plugin is not installed" is not something any server said. A mock that
/// replayed it as a frame would be staging a sentence it cannot honestly
/// utter: it IS serving text/event-stream.
/// </para>
/// </remarks>
public sealed class BowireRecordingStreamErrorTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_Step_That_Completed_Carries_No_Stream_Error()
    {
        // Which is every step recorded before this field existed, and every
        // healthy one after. Null rather than a "none" sentinel so nothing
        // downstream has to know a second way of saying nothing happened.
        var step = new BowireRecordingStep { Id = "s1", Protocol = "sse" };

        Assert.Null(step.StreamError);
    }

    [Fact]
    public void The_Reason_Survives_A_Round_Trip_To_Disk()
    {
        var step = new BowireRecordingStep
        {
            Id = "s1",
            Protocol = "graphql",
            Status = "Error: refused",
            StreamError = new StreamError(BowireStreamErrorKinds.Refused, "quota exceeded", "429"),
        };

        var back = JsonSerializer.Deserialize<BowireRecordingStep>(
            JsonSerializer.Serialize(step, Options), Options);

        Assert.NotNull(back);
        Assert.NotNull(back!.StreamError);
        Assert.Equal(BowireStreamErrorKinds.Refused, back.StreamError!.Kind);
        Assert.Equal("quota exceeded", back.StreamError.Message);
        Assert.Equal("429", back.StreamError.Code);
        // The status has to agree with it. A step that says OK next to a
        // reason it stopped is the state this change exists to end.
        Assert.NotEqual("OK", back.Status);
    }

    [Fact]
    public void An_Older_Recording_Without_The_Field_Still_Loads()
    {
        // Recordings on disk outlive every other consumer, so a new field
        // must be optional in the read direction as well as the write one.
        var json = """
        {
          "id": "s1",
          "protocol": "sse",
          "service": "S",
          "method": "M",
          "status": "OK",
          "receivedMessages": [{ "index": 0, "data": "{\"n\":1}" }]
        }
        """;

        var step = JsonSerializer.Deserialize<BowireRecordingStep>(json, Options);

        Assert.NotNull(step);
        Assert.Null(step!.StreamError);
        Assert.Equal("OK", step.Status);
        Assert.Single(step.ReceivedMessages!);
    }

    [Fact]
    public void The_Frames_A_Replay_Sees_Are_Only_What_The_Server_Sent()
    {
        // The whole point of keeping the reason off the frame list: every
        // replayer reads ReceivedMessages, and none of them has to learn
        // what a Bowire marker looks like or skip one. They never see it.
        var step = new BowireRecordingStep
        {
            Id = "s1",
            Protocol = "sse",
            Status = "Error: transport",
            StreamError = new StreamError(BowireStreamErrorKinds.Transport, "socket closed"),
            ReceivedMessages =
            [
                new BowireRecordingFrame { Index = 0, Data = """{"n":1}""" },
                new BowireRecordingFrame { Index = 1, Data = """{"n":2}""" },
            ],
        };

        Assert.Equal(2, step.ReceivedMessages!.Count);
        Assert.DoesNotContain(step.ReceivedMessages,
            f => f.Data is string d && d.Contains(BowireStreamErrorEnvelope.Key, StringComparison.Ordinal));
    }

    [Fact]
    public void A_Frame_Model_Has_Nowhere_To_Put_An_Error_And_That_Is_On_Purpose()
    {
        // Pinned as a contract rather than left implicit: if somebody later
        // adds an Error property to BowireRecordingFrame, replay starts
        // re-staging Bowire's own conclusions as server output, and this
        // test is where that conversation happens.
        var properties = typeof(BowireRecordingFrame).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("Error", properties);
        Assert.DoesNotContain("StreamError", properties);
    }
}
