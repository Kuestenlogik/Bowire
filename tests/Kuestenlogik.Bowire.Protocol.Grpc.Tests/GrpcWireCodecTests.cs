// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Kuestenlogik.Bowire.Protocol.Grpc.Mock;
using Kuestenlogik.Bowire.Protocol.Grpc.Tests.Proto;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Tests;

/// <summary>
/// The descriptor-driven JSON &lt;-&gt; protobuf codec in <c>GrpcInvoker</c>,
/// held against Google.Protobuf as the ground truth. Decoding takes the
/// bytes a generated message writes and must give JSON that Google.Protobuf
/// parses back into the same message; encoding takes JSON and must give
/// bytes that Google.Protobuf parses into the expected message.
/// </summary>
public sealed class GrpcWireCodecTests
{
    private static T Decode<T>(IMessage message) where T : IMessage<T>, new()
    {
        var json = GrpcInvoker.FormatResponsePublic(message.ToByteArray(), message.Descriptor);
        return JsonParser.Default.Parse<T>(json);
    }

    private static T Encode<T>(string json, MessageParser<T> parser) where T : IMessage<T>, new()
    {
        var bytes = GrpcInvoker.JsonToProtobufPublic(json, new T().Descriptor);
        return parser.ParseFrom(bytes);
    }

    // ---- packed repeated scalars (#889) -----------------------------

    [Fact]
    public void Decode_reads_packed_repeated_scalars()
    {
        var expected = new WirePacked
        {
            I32 = { 1, -2, 300 },
            I64 = { 1L << 40, -5 },
            U64 = { 7, 1UL << 50 },
            D = { 1.5, -2.25 },
            F = { 0.5f },
            B = { true, false, true },
            Colors = { WireColor.Red, WireColor.Green },
            Names = { "a", "b" },
        };

        Assert.Equal(expected, Decode<WirePacked>(expected));
    }

    // ---- sint32 / sint64 zigzag (#890) ------------------------------

    [Fact]
    public void Decode_reads_sint_fields_zigzag_encoded()
    {
        var expected = new WireZigZag { Si32 = -1, Si64 = -2, Many = { -3, 4 } };

        Assert.Equal(expected, Decode<WireZigZag>(expected));
    }

    [Fact]
    public void Encode_writes_sint_fields_zigzag_encoded()
    {
        var actual = Encode("""{"si32":-1,"si64":-2,"many":[-3,4]}""", WireZigZag.Parser);

        Assert.Equal(new WireZigZag { Si32 = -1, Si64 = -2, Many = { -3, 4 } }, actual);
    }

    [Fact]
    public void Sample_encoder_writes_sint_fields_zigzag_encoded()
    {
        var sample = WireZigZag.Parser.ParseFrom(ProtobufSampleEncoder.Encode(WireZigZag.Descriptor));

        Assert.Equal(1, sample.Si32);
        Assert.Equal(1L, sample.Si64);
        Assert.All(sample.Many, v => Assert.Equal(1, v));
    }

    // ---- fixed32 / fixed64 / sfixed32 / sfixed64 (#891) -------------

    [Fact]
    public void Decode_reads_fixed_and_sfixed_fields()
    {
        var expected = new WireFixed
        {
            F32 = uint.MaxValue,
            F64 = 1UL << 50,
            Sf32 = -7,
            Sf64 = -(1L << 40),
            Many = { 1, 2 },
        };

        Assert.Equal(expected, Decode<WireFixed>(expected));
    }

    [Fact]
    public void Encode_writes_fixed_and_sfixed_fields()
    {
        var actual = Encode(
            """{"f32":4294967295,"f64":1125899906842624,"sf32":-7,"sf64":-1099511627776,"many":[1,2]}""",
            WireFixed.Parser);

        Assert.Equal(new WireFixed
        {
            F32 = uint.MaxValue,
            F64 = 1UL << 50,
            Sf32 = -7,
            Sf64 = -(1L << 40),
            Many = { 1, 2 },
        }, actual);
    }

    // ---- integers given as JSON strings (#894) --------------------------

    [Fact]
    public void Encode_reads_64_bit_integers_from_their_canonical_JSON_strings()
    {
        // proto3 JSON writes every 64-bit integer as a string; this is
        // Google.Protobuf's own rendering of the message.
        var expected = new WireWide
        {
            I64 = long.MinValue,
            U64 = ulong.MaxValue,
            Si64 = -9_007_199_254_740_993,
            F64 = ulong.MaxValue - 1,
            Sf64 = long.MaxValue,
            I32 = -5,
            U32 = 7,
        };
        var json = JsonFormatter.Default.Format(expected);

        Assert.Equal(expected, Encode(json, WireWide.Parser));
    }

    [Fact]
    public void Encode_reads_32_bit_integers_from_JSON_strings()
    {
        // A proto3 JSON parser accepts the string form for every integer type.
        var actual = Encode("""{"i32":"-5","u32":"7"}""", WireWide.Parser);

        Assert.Equal(new WireWide { I32 = -5, U32 = 7 }, actual);
    }
}
