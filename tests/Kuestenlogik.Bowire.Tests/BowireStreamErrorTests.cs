// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The stream contract's error form (#712).
/// </summary>
/// <remarks>
/// <para>
/// Before this the contract had no such form, so twelve sites across five
/// plugins invented four shapes between them. The one that matters most
/// here is the boundary: the reserved key exists precisely because a bare
/// <c>error</c> key is something several servers put in their own payloads,
/// and a consumer must be able to tell "the stream ended" from "the server
/// sent a message that mentions an error".
/// </para>
/// </remarks>
public sealed class BowireStreamErrorTests
{
    [Fact]
    public void A_Frame_Round_Trips_Its_Kind_Message_And_Code()
    {
        var frame = BowireStreamErrorEnvelope.Frame(
            new StreamError(BowireStreamErrorKinds.Refused, "quota exceeded", "429"));

        var read = BowireStreamErrorEnvelope.TryRead(frame);

        Assert.NotNull(read);
        Assert.Equal(BowireStreamErrorKinds.Refused, read!.Kind);
        Assert.Equal("quota exceeded", read.Message);
        Assert.Equal("429", read.Code);
    }

    [Fact]
    public void A_Protocol_Without_Codes_Leaves_Code_Null_Rather_Than_Empty()
    {
        var read = BowireStreamErrorEnvelope.TryRead(
            BowireStreamErrorEnvelope.Frame(BowireStreamErrorKinds.Idle, "nothing arrived"));

        Assert.NotNull(read);
        Assert.Null(read!.Code);
    }

    [Fact]
    public void An_Ordinary_Payload_That_Talks_About_Errors_Is_Still_Payload()
    {
        // The whole reason the key is reserved and ugly. Every one of these
        // is a shape some plugin in this repo emits as data today.
        Assert.Null(BowireStreamErrorEnvelope.TryRead("""{"error":"boom"}"""));
        Assert.Null(BowireStreamErrorEnvelope.TryRead("""{"errors":[{"message":"boom"}]}"""));
        Assert.Null(BowireStreamErrorEnvelope.TryRead("""{"data":{"log":"error: disk full"}}"""));
        Assert.Null(BowireStreamErrorEnvelope.TryRead("""{"status":"error","kind":"transport"}"""));
    }

    [Fact]
    public void A_Frame_That_Is_Not_Json_Is_Payload_Not_An_Error()
    {
        // A plugin may stream whatever a server sent. Reading unparseable
        // output as "the stream failed" would put words in the server's
        // mouth — and would turn a text/plain stream into a permanent
        // failure.
        Assert.Null(BowireStreamErrorEnvelope.TryRead("not json at all"));
        Assert.Null(BowireStreamErrorEnvelope.TryRead("[1,2,3]"));
        Assert.Null(BowireStreamErrorEnvelope.TryRead("\"a bare string\""));
        Assert.Null(BowireStreamErrorEnvelope.TryRead(""));
        Assert.Null(BowireStreamErrorEnvelope.TryRead(null));
    }

    [Fact]
    public void The_Reserved_Key_Without_A_Message_Is_Not_An_Error_Either()
    {
        // A message is what makes the error actionable. Rather than invent
        // wording on a plugin's behalf, treat the frame as data — which is
        // at least honest about not knowing.
        Assert.Null(BowireStreamErrorEnvelope.TryRead(
            Envelope("\"kind\":\"transport\"")));
        Assert.Null(BowireStreamErrorEnvelope.TryRead(
            Envelope("\"kind\":\"transport\",\"message\":\"  \"")));
    }

    [Fact]
    public void A_Kind_Nobody_Declared_Survives_Rather_Than_Being_Normalised()
    {
        // The constants are a vocabulary, not an enum. A plugin whose
        // protocol has a failure mode core never thought of names it, and
        // the consumer treats any error as a failure regardless — so an
        // unknown word must not be rewritten into a known one, and above
        // all must not fall through to success.
        var read = BowireStreamErrorEnvelope.TryRead(
            BowireStreamErrorEnvelope.Frame("backpressure", "consumer too slow"));

        Assert.NotNull(read);
        Assert.Equal("backpressure", read!.Kind);
    }

    [Fact]
    public void A_Missing_Kind_Falls_Back_Rather_Than_Being_Dropped()
    {
        // A plugin that names no kind still has something to report, and
        // losing the whole frame over a missing label would be worse than
        // labelling it conservatively.
        var read = BowireStreamErrorEnvelope.TryRead(
            Envelope("\"message\":\"socket closed\""));

        Assert.NotNull(read);
        Assert.Equal(BowireStreamErrorKinds.Transport, read!.Kind);
        Assert.Equal("socket closed", read.Message);
    }

    [Fact]
    public void The_Envelope_Is_Camel_Cased_Because_The_Browser_Reads_It()
    {
        // The SSE envelope goes to api.js, which reads `kind` and
        // `message`. Pascal case here would be a silent mismatch: the
        // frame would parse and every field would be undefined.
        using var doc = JsonDocument.Parse(
            BowireStreamErrorEnvelope.Frame(BowireStreamErrorKinds.Server, "nope", "500"));
        var node = doc.RootElement.GetProperty(BowireStreamErrorEnvelope.Key);

        Assert.True(node.TryGetProperty("kind", out _));
        Assert.True(node.TryGetProperty("message", out _));
        Assert.True(node.TryGetProperty("code", out _));
    }

    [Fact]
    public void A_Frame_With_Wire_Bytes_Carries_Its_Error_In_A_Typed_Slot()
    {
        // Plugins on IBowireStreamingWithWireBytes do not need the
        // envelope; the record has somewhere to put it. gRPC's private
        // ConnectStreamFrame.ErrorCode is what this replaces.
        var frame = new StreamFrame("{}", null)
        {
            Error = new StreamError(BowireStreamErrorKinds.Server, "RESOURCE_EXHAUSTED", "8"),
        };

        Assert.NotNull(frame.Error);
        Assert.Equal("8", frame.Error!.Code);
        // And an ordinary frame says nothing, so nothing downstream has to
        // guess what "no error" looks like.
        Assert.Null(new StreamFrame("{}", null).Error);
    }

    /// <summary>
    /// A raw frame carrying the reserved key, for the cases that have to
    /// hand-build one the <c>Frame</c> helper would never produce.
    /// </summary>
    private static string Envelope(string innerJson)
        => "{\"" + BowireStreamErrorEnvelope.Key + "\":{" + innerJson + "}}";
}
