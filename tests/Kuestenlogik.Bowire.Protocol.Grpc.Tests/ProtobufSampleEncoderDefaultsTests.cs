// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Kuestenlogik.Bowire.Protocol.Grpc.Mock;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Tests;

/// <summary>
/// #559: <see cref="ProtobufSampleEncoder"/> honours a proto2 field default
/// (<c>[default = ...]</c>) — a value the schema author declared — in
/// preference to the type-driven sample. proto3 has no field-level defaults,
/// so it keeps emitting the type sample.
/// </summary>
public sealed class ProtobufSampleEncoderDefaultsTests
{
    private static MessageDescriptor BuildMessage(string syntax, params FieldDescriptorProto[] fields)
    {
        var fd = new FileDescriptorProto
        {
            Name = $"demo/{Guid.NewGuid():N}.proto",
            Package = "demo",
            Syntax = syntax,
        };
        var msg = new DescriptorProto { Name = "M" };
        foreach (var f in fields) msg.Field.Add(f);
        fd.MessageType.Add(msg);

        var descriptors = FileDescriptor.BuildFromByteStrings([fd.ToByteString()]);
        return descriptors[0].MessageTypes.Single(m => m.Name == "M");
    }

    [Fact]
    public void Encode_Emits_Proto2_String_Default_Over_Type_Sample()
    {
        var msg = BuildMessage("proto2", new FieldDescriptorProto
        {
            Name = "message",
            Number = 1,
            Type = FieldDescriptorProto.Types.Type.String,
            Label = FieldDescriptorProto.Types.Label.Optional,
            DefaultValue = "hi-default",
        });

        var bytes = ProtobufSampleEncoder.Encode(msg);

        // Field 1, length-delimited: tag 0x0A, length, then the declared default.
        Assert.Equal(0x0A, bytes[0]);
        Assert.Equal((byte)"hi-default".Length, bytes[1]);
        Assert.Equal("hi-default", Encoding.UTF8.GetString(bytes, 2, "hi-default".Length));
    }

    [Fact]
    public void Encode_Emits_Proto2_Int_Default_Over_Type_Sample()
    {
        var msg = BuildMessage("proto2", new FieldDescriptorProto
        {
            Name = "n",
            Number = 1,
            Type = FieldDescriptorProto.Types.Type.Int32,
            Label = FieldDescriptorProto.Types.Label.Optional,
            DefaultValue = "42",
        });

        var bytes = ProtobufSampleEncoder.Encode(msg);

        // Field 1, varint: tag 0x08, value 42 (0x2A) — not the type-default 1.
        Assert.Equal(0x08, bytes[0]);
        Assert.Equal(42, bytes[1]);
    }

    [Fact]
    public void Encode_Proto3_Without_Default_Uses_Type_Sample()
    {
        // Control: proto3 has no field-level defaults → the "sample" placeholder.
        var msg = BuildMessage("proto3", new FieldDescriptorProto
        {
            Name = "message",
            Number = 1,
            Type = FieldDescriptorProto.Types.Type.String,
            Label = FieldDescriptorProto.Types.Label.Optional,
        });

        var bytes = ProtobufSampleEncoder.Encode(msg);

        Assert.Equal(0x0A, bytes[0]);
        Assert.Equal("sample", Encoding.UTF8.GetString(bytes, 2, 6));
    }

    [Fact]
    public void Encode_SInt32_Default_Is_ZigZag_Encoded()
    {
        // sint* fields are zigzag-encoded — a conforming client must decode 5.
        var msg = BuildMessage("proto2", new FieldDescriptorProto
        {
            Name = "delta",
            Number = 1,
            Type = FieldDescriptorProto.Types.Type.Sint32,
            Label = FieldDescriptorProto.Types.Label.Optional,
            DefaultValue = "5",
        });

        var value = ReadSingleField(ProtobufSampleEncoder.Encode(msg), i => i.ReadSInt32());
        Assert.Equal(5, value);
    }

    [Fact]
    public void Encode_UInt64_Default_Above_Long_Max()
    {
        var msg = BuildMessage("proto2", new FieldDescriptorProto
        {
            Name = "big",
            Number = 1,
            Type = FieldDescriptorProto.Types.Type.Uint64,
            Label = FieldDescriptorProto.Types.Label.Optional,
            DefaultValue = "18446744073709551615", // ulong.MaxValue
        });

        var value = ReadSingleField(ProtobufSampleEncoder.Encode(msg), i => i.ReadUInt64());
        Assert.Equal(ulong.MaxValue, value);
    }

    [Fact]
    public void Encode_Double_Infinity_Default()
    {
        var msg = BuildMessage("proto2", new FieldDescriptorProto
        {
            Name = "ratio",
            Number = 1,
            Type = FieldDescriptorProto.Types.Type.Double,
            Label = FieldDescriptorProto.Types.Label.Optional,
            DefaultValue = "inf",
        });

        var value = ReadSingleField(ProtobufSampleEncoder.Encode(msg), i => i.ReadDouble());
        Assert.Equal(double.PositiveInfinity, value);
    }

    [Fact]
    public void Encode_Bool_Default()
    {
        var msg = BuildMessage("proto2", new FieldDescriptorProto
        {
            Name = "flag",
            Number = 1,
            Type = FieldDescriptorProto.Types.Type.Bool,
            Label = FieldDescriptorProto.Types.Label.Optional,
            DefaultValue = "true",
        });

        var value = ReadSingleField(ProtobufSampleEncoder.Encode(msg), i => i.ReadBool());
        Assert.True(value);
    }

    [Fact]
    public void Encode_Enum_Default_By_Value_Name()
    {
        var fd = new FileDescriptorProto
        {
            Name = $"demo/{Guid.NewGuid():N}.proto",
            Package = "demo",
            Syntax = "proto2",
        };
        fd.EnumType.Add(new EnumDescriptorProto
        {
            Name = "Color",
            Value =
            {
                new EnumValueDescriptorProto { Name = "RED", Number = 0 },
                new EnumValueDescriptorProto { Name = "GREEN", Number = 1 },
                new EnumValueDescriptorProto { Name = "BLUE", Number = 2 },
            },
        });
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "M",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "color",
                    Number = 1,
                    Type = FieldDescriptorProto.Types.Type.Enum,
                    TypeName = ".demo.Color",
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    DefaultValue = "GREEN",
                },
            },
        });
        var msg = FileDescriptor.BuildFromByteStrings([fd.ToByteString()])[0].MessageTypes.Single(m => m.Name == "M");

        var value = ReadSingleField(ProtobufSampleEncoder.Encode(msg), i => i.ReadEnum());
        Assert.Equal(1, value); // GREEN
    }

    // Decode the first field of a single-field message so the assertion is on
    // the value a conforming client actually reads (not the raw wire bytes).
    private static T ReadSingleField<T>(byte[] bytes, Func<CodedInputStream, T> read)
    {
        using var input = new CodedInputStream(bytes);
        input.ReadTag();
        return read(input);
    }
}
