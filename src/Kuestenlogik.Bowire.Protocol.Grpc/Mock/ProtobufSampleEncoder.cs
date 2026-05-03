// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Mock;

/// <summary>
/// Hand-rolled protobuf wire-format encoder that emits a plausible
/// sample instance of a message given only its <see cref="MessageDescriptor"/>.
/// Complements <c>Kuestenlogik.Bowire.Protocol.Rest.Mock.OpenApiSampleGenerator</c> for the gRPC
/// schema-only mock path: instead of JSON text, we produce the raw
/// binary bytes the gRPC framer wraps in a length-prefixed envelope.
/// </summary>
/// <remarks>
/// <para>
/// Google.Protobuf's C# library has no <c>DynamicMessage</c>
/// equivalent — the standard way to serialize a protobuf is through
/// a code-generated message class. For a schema-only mock we don't
/// have those classes; we only have the parsed
/// <see cref="FileDescriptor"/>s from a <c>protoc
/// --descriptor_set_out</c> output. This encoder walks the
/// descriptor directly and writes wire bytes with
/// <see cref="CodedOutputStream"/>, which is the same primitive the
/// generated code ultimately calls.
/// </para>
/// <para>
/// Per-field sample values match the OpenAPI generator's instincts:
/// <c>int*</c>/<c>sint*</c>/<c>fixed*</c> → <c>1</c>,
/// <c>float</c>/<c>double</c> → <c>1.5</c>, <c>string</c>/<c>bytes</c>
/// → <c>"sample"</c>, <c>bool</c> → <c>true</c>, enums → the zero
/// value (the proto3-default), nested messages → recurse. Repeated
/// fields carry three entries. Cyclic <c>$ref</c>-style schemas are
/// tamed by a depth cap.
/// </para>
/// </remarks>
public static class ProtobufSampleEncoder
{
    private const int MaxDepth = 8;
    private const int RepeatedCount = 3;

    /// <summary>Encode one sample instance of <paramref name="descriptor"/> as wire bytes.</summary>
    public static byte[] Encode(MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            EncodeMessage(output, descriptor, depth: 0);
        }
        return stream.ToArray();
    }

    private static void EncodeMessage(CodedOutputStream output, MessageDescriptor descriptor, int depth)
    {
        if (depth >= MaxDepth) return;

        foreach (var field in descriptor.Fields.InDeclarationOrder())
        {
            // proto3 optional / presence is flagged by the HasPresence
            // flag on the descriptor; for sample generation we emit
            // every scalar anyway so clients see populated fields.
            // Skip fields we clearly can't represent (FieldType.Group is
            // proto2 only and effectively dead).
            if (field.FieldType == FieldType.Group) continue;

            if (field.IsMap)
            {
                EncodeMapField(output, field, depth);
                continue;
            }

            if (field.IsRepeated)
            {
                for (var i = 0; i < RepeatedCount; i++)
                {
                    EncodeSingleValue(output, field, depth);
                }
            }
            else
            {
                EncodeSingleValue(output, field, depth);
            }
        }
    }

    // Proto3 maps are represented as `repeated MapEntry { K key = 1; V value = 2; }`
    // on the wire — same encoding as any other repeated nested message.
    private static void EncodeMapField(CodedOutputStream output, FieldDescriptor field, int depth)
    {
        var entryDescriptor = field.MessageType;
        if (entryDescriptor is null) return;

        // Emit three map entries to match the repeated-sample convention.
        for (var i = 0; i < RepeatedCount; i++)
        {
            using var entryStream = new MemoryStream();
            using (var entryOutput = new CodedOutputStream(entryStream, leaveOpen: true))
            {
                EncodeMessage(entryOutput, entryDescriptor, depth + 1);
            }
            var bytes = entryStream.ToArray();

            output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(bytes));
        }
    }

    private static void EncodeSingleValue(CodedOutputStream output, FieldDescriptor field, int depth)
    {
        switch (field.FieldType)
        {
            case FieldType.Int32:
            case FieldType.Int64:
            case FieldType.UInt32:
            case FieldType.UInt64:
            case FieldType.SInt32:
            case FieldType.SInt64:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteInt64(1);
                break;

            case FieldType.Bool:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteBool(true);
                break;

            case FieldType.Enum:
                // Proto3 enums always start at 0 (language requirement).
                // Emit the first declared value to be safe — may be
                // non-zero in proto2-compatible files, but the
                // descriptor order is stable.
                var enumValue = field.EnumType?.Values.FirstOrDefault()?.Number ?? 0;
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteInt32(enumValue);
                break;

            case FieldType.Fixed32:
            case FieldType.SFixed32:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed32);
                output.WriteFixed32(1);
                break;

            case FieldType.Fixed64:
            case FieldType.SFixed64:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed64);
                output.WriteFixed64(1);
                break;

            case FieldType.Float:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed32);
                output.WriteFloat(1.5f);
                break;

            case FieldType.Double:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed64);
                output.WriteDouble(1.5);
                break;

            case FieldType.String:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteString("sample");
                break;

            case FieldType.Bytes:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFromUtf8("sample"));
                break;

            case FieldType.Message:
                if (field.MessageType is null) return;
                // Recurse into the nested message, serialize to a
                // separate buffer so we can prefix it with the
                // length-delimited wire tag.
                using (var nestedStream = new MemoryStream())
                {
                    using (var nestedOutput = new CodedOutputStream(nestedStream, leaveOpen: true))
                    {
                        EncodeMessage(nestedOutput, field.MessageType, depth + 1);
                    }
                    var bytes = nestedStream.ToArray();
                    output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                    output.WriteBytes(ByteString.CopyFrom(bytes));
                }
                break;

            case FieldType.Group:
                // Proto2-only, not worth supporting in a mock — fall through.
                break;
        }
    }
}
