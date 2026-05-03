// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Tests;

public class ModelTests
{
    [Fact]
    public void BowireServiceInfo_Record_Works()
    {
        var methods = new List<BowireMethodInfo>
        {
            new(
                Name: "SayHello",
                FullName: "greet.Greeter/SayHello",
                ClientStreaming: false,
                ServerStreaming: false,
                InputType: new BowireMessageInfo("HelloRequest", "greet.HelloRequest", []),
                OutputType: new BowireMessageInfo("HelloReply", "greet.HelloReply", []),
                MethodType: "Unary")
        };

        var service = new BowireServiceInfo("greet.Greeter", "greet", methods);

        Assert.Equal("greet.Greeter", service.Name);
        Assert.Equal("greet", service.Package);
        Assert.Single(service.Methods);
        Assert.Equal("SayHello", service.Methods[0].Name);
    }

    [Fact]
    public void BowireMethodInfo_MethodTypes()
    {
        var emptyMsg = new BowireMessageInfo("Empty", "google.protobuf.Empty", []);

        var unary = new BowireMethodInfo("Call", "svc/Call", false, false, emptyMsg, emptyMsg, "Unary");
        var serverStream = new BowireMethodInfo("Stream", "svc/Stream", false, true, emptyMsg, emptyMsg, "ServerStreaming");
        var clientStream = new BowireMethodInfo("Upload", "svc/Upload", true, false, emptyMsg, emptyMsg, "ClientStreaming");
        var duplex = new BowireMethodInfo("Chat", "svc/Chat", true, true, emptyMsg, emptyMsg, "Duplex");

        Assert.False(unary.ClientStreaming);
        Assert.False(unary.ServerStreaming);
        Assert.True(serverStream.ServerStreaming);
        Assert.True(clientStream.ClientStreaming);
        Assert.True(duplex.ClientStreaming);
        Assert.True(duplex.ServerStreaming);
    }

    [Fact]
    public void BowireFieldInfo_With_Nested_Types()
    {
        var innerFields = new List<BowireFieldInfo>
        {
            new("value", 1, "string", "optional", false, false, null, null)
        };
        var innerMsg = new BowireMessageInfo("Inner", "pkg.Inner", innerFields);

        var field = new BowireFieldInfo(
            Name: "nested",
            Number: 1,
            Type: "message",
            Label: "optional",
            IsMap: false,
            IsRepeated: false,
            MessageType: innerMsg,
            EnumValues: null);

        Assert.Equal("message", field.Type);
        Assert.NotNull(field.MessageType);
        Assert.Single(field.MessageType.Fields);
        Assert.Equal("value", field.MessageType.Fields[0].Name);
    }

    [Fact]
    public void BowireFieldInfo_With_Enum()
    {
        var enumValues = new List<BowireEnumValue>
        {
            new("UNKNOWN", 0),
            new("VALUE_A", 1),
            new("VALUE_B", 2)
        };

        var field = new BowireFieldInfo(
            Name: "status",
            Number: 1,
            Type: "enum",
            Label: "optional",
            IsMap: false,
            IsRepeated: false,
            MessageType: null,
            EnumValues: enumValues);

        Assert.Equal("enum", field.Type);
        Assert.NotNull(field.EnumValues);
        Assert.Equal(3, field.EnumValues.Count);
        Assert.Equal("UNKNOWN", field.EnumValues[0].Name);
        Assert.Equal(0, field.EnumValues[0].Number);
    }

    [Fact]
    public void BowireFieldInfo_Repeated_And_Map()
    {
        var repeated = new BowireFieldInfo("items", 1, "string", "repeated", false, true, null, null);
        var map = new BowireFieldInfo("labels", 2, "message", "repeated", true, false, null, null);

        Assert.True(repeated.IsRepeated);
        Assert.False(repeated.IsMap);
        Assert.True(map.IsMap);
        Assert.False(map.IsRepeated);
    }
}
