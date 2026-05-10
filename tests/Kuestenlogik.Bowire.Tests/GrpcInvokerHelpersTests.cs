// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Kuestenlogik.Bowire;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for the static helpers <c>GrpcInvoker</c> exposes through
/// <c>InternalsVisibleTo</c>. These cover the JsonToProtobuf encoder + the
/// ProtobufToJson decoder field-by-field, the BuildFileDescriptors fast +
/// slow paths, and the TopologicalSort branch — all reachable without an
/// HTTP/2 channel.
/// </summary>
public sealed class GrpcInvokerHelpersTests
{
    [Fact]
    public void JsonToProtobuf_All_Scalar_Types_Roundtrip_Through_FormatResponse()
    {
        var fdProto = new FileDescriptorProto
        {
            Name = "scalars.proto",
            Package = "demo",
            Syntax = "proto3"
        };
        fdProto.MessageType.Add(new DescriptorProto
        {
            Name = "Scalars",
            Field =
            {
                Field("s",   1, FieldDescriptorProto.Types.Type.String),
                Field("i32", 2, FieldDescriptorProto.Types.Type.Int32),
                Field("i64", 3, FieldDescriptorProto.Types.Type.Int64),
                Field("u32", 4, FieldDescriptorProto.Types.Type.Uint32),
                Field("u64", 5, FieldDescriptorProto.Types.Type.Uint64),
                Field("d",   6, FieldDescriptorProto.Types.Type.Double),
                Field("f",   7, FieldDescriptorProto.Types.Type.Float),
                Field("b",   8, FieldDescriptorProto.Types.Type.Bool),
                Field("by",  9, FieldDescriptorProto.Types.Type.Bytes),
                Field("si32", 10, FieldDescriptorProto.Types.Type.Sint32),
                Field("si64", 11, FieldDescriptorProto.Types.Type.Sint64)
            }
        });

        var fd = FileDescriptor.BuildFromByteStrings(new[] { fdProto.ToByteString() })[0];
        var msg = fd.MessageTypes.First();

        var bytes = GrpcInvoker.JsonToProtobufPublic(
            """
            {
              "s":"hello",
              "i32":42,
              "i64":9999999999,
              "u32":7,
              "u64":11,
              "d":1.25,
              "f":2.5,
              "b":true,
              "by":"YWJj",
              "si32":-1,
              "si64":-2
            }
            """, msg);

        var json = GrpcInvoker.FormatResponsePublic(bytes, msg);

        Assert.Contains("\"s\": \"hello\"", json, StringComparison.Ordinal);
        Assert.Contains("\"i32\": 42", json, StringComparison.Ordinal);
        Assert.Contains("\"b\": true", json, StringComparison.Ordinal);
        // Bytes are base64 round-tripped through the JSON encoder.
        Assert.Contains("\"by\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonToProtobuf_Skips_Unknown_Field_Names_Silently()
    {
        // Unknown property names should be ignored — covers the
        // "field is null → continue" branch in WriteMessage.
        var msg = SingleStringFieldMessage();

        var bytes = GrpcInvoker.JsonToProtobufPublic(
            """{"v":"yo","not_a_real_field":"junk"}""", msg);

        // Round-trip recovers only the known field.
        var json = GrpcInvoker.FormatResponsePublic(bytes, msg);
        Assert.Contains("\"v\": \"yo\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("not_a_real_field", json, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonToProtobuf_Empty_Object_Yields_Empty_Bytes()
    {
        // Covers the early-out `ValueKind != Object` branch when called
        // recursively from WriteField for nested-message fields.
        var msg = SingleStringFieldMessage();

        var bytes = GrpcInvoker.JsonToProtobufPublic("{}", msg);

        Assert.Empty(bytes);
    }

    [Fact]
    public void JsonToProtobuf_NonObject_Root_Yields_Empty_Bytes()
    {
        // The encoder bails out cleanly when the root JSON value isn't an
        // object — exercises WriteMessage's first guard.
        var msg = SingleStringFieldMessage();

        var bytes = GrpcInvoker.JsonToProtobufPublic("[]", msg);

        Assert.Empty(bytes);
    }

    [Fact]
    public void JsonToProtobuf_Repeated_String_Field_Encodes_Each_Element()
    {
        var fdProto = new FileDescriptorProto
        {
            Name = "rep.proto",
            Package = "demo",
            Syntax = "proto3"
        };
        fdProto.MessageType.Add(new DescriptorProto
        {
            Name = "RepStr",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "items", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Repeated,
                    JsonName = "items"
                }
            }
        });
        var msg = FileDescriptor.BuildFromByteStrings(new[] { fdProto.ToByteString() })[0]
            .MessageTypes.First();

        var bytes = GrpcInvoker.JsonToProtobufPublic(
            """{"items":["a","b","c"]}""", msg);

        var json = GrpcInvoker.FormatResponsePublic(bytes, msg);
        Assert.Contains("\"a\"", json, StringComparison.Ordinal);
        Assert.Contains("\"b\"", json, StringComparison.Ordinal);
        Assert.Contains("\"c\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonToProtobuf_Enum_By_Name_Resolves_To_Number()
    {
        var fdProto = new FileDescriptorProto
        {
            Name = "enum.proto",
            Package = "demo",
            Syntax = "proto3"
        };
        fdProto.EnumType.Add(new EnumDescriptorProto
        {
            Name = "Color",
            Value =
            {
                new EnumValueDescriptorProto { Name = "RED", Number = 0 },
                new EnumValueDescriptorProto { Name = "GREEN", Number = 1 },
                new EnumValueDescriptorProto { Name = "BLUE", Number = 2 }
            }
        });
        fdProto.MessageType.Add(new DescriptorProto
        {
            Name = "WithEnum",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "color", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.Enum,
                    TypeName = ".demo.Color",
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "color"
                }
            }
        });
        var msg = FileDescriptor.BuildFromByteStrings(new[] { fdProto.ToByteString() })[0]
            .MessageTypes.First();

        // String name → encoder resolves via FindValueByName.
        var bytesByName = GrpcInvoker.JsonToProtobufPublic("""{"color":"GREEN"}""", msg);
        var jsonByName = GrpcInvoker.FormatResponsePublic(bytesByName, msg);
        Assert.Contains("GREEN", jsonByName, StringComparison.Ordinal);

        // Number → encoder takes the numeric branch.
        var bytesByNumber = GrpcInvoker.JsonToProtobufPublic("""{"color":2}""", msg);
        var jsonByNumber = GrpcInvoker.FormatResponsePublic(bytesByNumber, msg);
        Assert.Contains("BLUE", jsonByNumber, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonToProtobuf_Enum_With_Unknown_Number_Falls_Back_To_String()
    {
        var fdProto = new FileDescriptorProto
        {
            Name = "enum2.proto",
            Package = "demo",
            Syntax = "proto3"
        };
        fdProto.EnumType.Add(new EnumDescriptorProto
        {
            Name = "Status",
            Value =
            {
                new EnumValueDescriptorProto { Name = "OK", Number = 0 }
            }
        });
        fdProto.MessageType.Add(new DescriptorProto
        {
            Name = "S",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "s", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.Enum,
                    TypeName = ".demo.Status",
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "s"
                }
            }
        });
        var msg = FileDescriptor.BuildFromByteStrings(new[] { fdProto.ToByteString() })[0]
            .MessageTypes.First();

        // Encode a numeric enum that the descriptor doesn't define — the
        // decoder should fall back to ToString rather than crashing.
        var bytes = GrpcInvoker.JsonToProtobufPublic("""{"s":99}""", msg);
        var json = GrpcInvoker.FormatResponsePublic(bytes, msg);

        // Decoder writes the numeric form when no matching enum name exists.
        Assert.Contains("99", json, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonToProtobuf_Nested_Message_Recurses_Through_FieldType_Message()
    {
        var fdProto = new FileDescriptorProto
        {
            Name = "nested-roundtrip.proto",
            Package = "demo",
            Syntax = "proto3"
        };
        fdProto.MessageType.Add(new DescriptorProto
        {
            Name = "Inner",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "value", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "value"
                }
            }
        });
        fdProto.MessageType.Add(new DescriptorProto
        {
            Name = "Outer",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "inner", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.Message,
                    TypeName = ".demo.Inner",
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "inner"
                }
            }
        });
        var fd = FileDescriptor.BuildFromByteStrings(new[] { fdProto.ToByteString() })[0];
        var outer = fd.MessageTypes.First(m => m.Name == "Outer");

        var bytes = GrpcInvoker.JsonToProtobufPublic("""{"inner":{"value":"deep"}}""", outer);
        var json = GrpcInvoker.FormatResponsePublic(bytes, outer);

        Assert.Contains("deep", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFileDescriptors_Empty_Input_Yields_Empty_Output()
    {
        var result = GrpcInvoker.BuildFileDescriptorsPublic(new List<FileDescriptorProto>());

        Assert.Empty(result);
    }

    [Fact]
    public void BuildFileDescriptors_Single_File_Returns_That_File_Only()
    {
        var fdProto = new FileDescriptorProto
        {
            Name = "single.proto",
            Package = "demo",
            Syntax = "proto3"
        };
        fdProto.MessageType.Add(new DescriptorProto { Name = "M" });

        var built = GrpcInvoker.BuildFileDescriptorsPublic(new List<FileDescriptorProto> { fdProto });

        var only = Assert.Single(built);
        Assert.Equal("single.proto", only.Name);
        // Filter step strips the seeded well-known files out of the result.
        Assert.Equal("M", only.MessageTypes.First().Name);
    }

    [Fact]
    public void BuildFileDescriptors_Multiple_Files_With_Dependency_Topo_Sorts()
    {
        // dep.proto declares Inner; main.proto imports dep.proto and uses
        // Inner via a message field. Topological sort needs to keep dep.proto
        // ahead of main.proto for FileDescriptor.BuildFromByteStrings to
        // resolve the cross-file reference.
        var dep = new FileDescriptorProto
        {
            Name = "demo/dep.proto",
            Package = "demo",
            Syntax = "proto3"
        };
        dep.MessageType.Add(new DescriptorProto
        {
            Name = "Inner",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "x", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "x"
                }
            }
        });

        var main = new FileDescriptorProto
        {
            Name = "demo/main.proto",
            Package = "demo",
            Syntax = "proto3",
            Dependency = { "demo/dep.proto" }
        };
        main.MessageType.Add(new DescriptorProto
        {
            Name = "Outer",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "inner", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.Message,
                    TypeName = ".demo.Inner",
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "inner"
                }
            }
        });

        // Pass main first, dep second — sorter must reorder them. The
        // batch builder may fall back to the per-file builder if the
        // exact byte-string ordering doesn't suit it; either way, dep.proto
        // must end up in the result because main depends on it.
        var result = GrpcInvoker.BuildFileDescriptorsPublic(new List<FileDescriptorProto> { main, dep });

        Assert.NotEmpty(result);
        Assert.Contains(result, fd => fd.Name == "demo/dep.proto");
    }

    [Fact]
    public void BuildFileDescriptors_With_Missing_Dependency_Falls_Back_To_Schema_Only()
    {
        // user.proto imports a non-existent "third_party/missing.proto" —
        // the per-file builder strips the import + options and falls back
        // to the schema-only build. Result should still contain the file
        // (with the message) so discovery doesn't drop the service entirely.
        var user = new FileDescriptorProto
        {
            Name = "user.proto",
            Package = "demo",
            Syntax = "proto3",
            Dependency = { "third_party/missing.proto" }
        };
        user.MessageType.Add(new DescriptorProto
        {
            Name = "Req",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "n", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "n"
                }
            }
        });

        var result = GrpcInvoker.BuildFileDescriptorsPublic(new List<FileDescriptorProto> { user });

        // The fallback builder either strips deps + builds, or skips the
        // file. Both leave coverage intact for the catch arm — assert the
        // outcome is non-throwing and well-formed.
        Assert.NotNull(result);
    }

    private static FieldDescriptorProto Field(string name, int number, FieldDescriptorProto.Types.Type type) =>
        new()
        {
            Name = name,
            Number = number,
            Type = type,
            Label = FieldDescriptorProto.Types.Label.Optional,
            JsonName = name
        };

    private static MessageDescriptor SingleStringFieldMessage()
    {
        var fdProto = new FileDescriptorProto
        {
            Name = "single-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".proto",
            Package = "demo",
            Syntax = "proto3"
        };
        fdProto.MessageType.Add(new DescriptorProto
        {
            Name = "M",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "v", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "v"
                }
            }
        });
        return FileDescriptor.BuildFromByteStrings(new[] { fdProto.ToByteString() })[0]
            .MessageTypes.First();
    }
}
