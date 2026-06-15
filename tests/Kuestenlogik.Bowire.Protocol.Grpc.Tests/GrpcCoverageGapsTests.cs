// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Kuestenlogik.Bowire.Protocol.Grpc.Mock;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Tests;

/// <summary>
/// Targeted gap-fillers for the Grpc plugin: ConnectInvoker's
/// pure-static helpers (URL normalisation + Connect-error envelope
/// parsing + end-of-stream JSON parsing) and the
/// <see cref="ProtobufRecordingBuilder"/> error branches the file-
/// loading test doesn't reach.
/// </summary>
public sealed class GrpcCoverageGapsTests
{
    private static readonly Type s_connectInvokerType =
        typeof(BowireGrpcProtocol).Assembly.GetType("Kuestenlogik.Bowire.Protocol.Grpc.ConnectInvoker")!;

    // ---- ConnectInvoker.NormaliseBaseUrl (private static) ------------

    [Theory]
    [InlineData("grpc://localhost:50051", "http://localhost:50051")]
    [InlineData("grpcs://api.example.com", "https://api.example.com")]
    [InlineData("GRPC://localhost", "http://localhost")]
    [InlineData("GRPCS://api", "https://api")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("https://api.example.com", "https://api.example.com")]
    public void NormaliseBaseUrl_RewritesGrpcSchemes_To_Http(string input, string expected)
    {
        var mi = s_connectInvokerType.GetMethod(
            "NormaliseBaseUrl", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (string)mi.Invoke(null, [input])!;
        Assert.Equal(expected, result);
    }

    // ---- ConnectInvoker.ParseConnectError (private static) ------------

    [Fact]
    public void ParseConnectError_ValidJson_ReturnsConnectCode_AndMessage()
    {
        // The connect: prefix lets recording consumers tell a Connect
        // error apart from a gRPC StatusCode when the two share names.
        var body = Encoding.UTF8.GetBytes("""{"code":"invalid_argument","message":"bad input"}""");
        using var resp = new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest);
        var (code, message) = InvokeParseConnectError(body, resp);
        Assert.Equal("connect:invalid_argument", code);
        Assert.Equal("bad input", message);
    }

    [Fact]
    public void ParseConnectError_MissingCodeField_FallsBack_To_HttpStatus()
    {
        var body = Encoding.UTF8.GetBytes("""{"message":"orphan"}""");
        using var resp = new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
        {
            ReasonPhrase = "Internal Server Error",
        };
        var (code, message) = InvokeParseConnectError(body, resp);
        Assert.Equal("http:500", code);
        Assert.Equal("Internal Server Error", message);
    }

    [Fact]
    public void ParseConnectError_NonStringCode_FallsBack_To_HttpStatus()
    {
        // Defensive: numeric `code` (which the Connect spec doesn't
        // allow) shouldn't be mis-parsed into a connect:42 code.
        var body = Encoding.UTF8.GetBytes("""{"code":42,"message":"x"}""");
        using var resp = new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest);
        var (code, _) = InvokeParseConnectError(body, resp);
        Assert.Equal("http:400", code);
    }

    [Fact]
    public void ParseConnectError_NonObjectBody_FallsBack_To_HttpStatus()
    {
        var body = Encoding.UTF8.GetBytes("\"plain string error\"");
        using var resp = new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        {
            ReasonPhrase = "Not Found",
        };
        var (code, message) = InvokeParseConnectError(body, resp);
        Assert.Equal("http:404", code);
        Assert.Equal("Not Found", message);
    }

    [Fact]
    public void ParseConnectError_MalformedJson_FallsBack_To_HttpStatus()
    {
        var body = Encoding.UTF8.GetBytes("not json");
        using var resp = new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway);
        var (code, _) = InvokeParseConnectError(body, resp);
        Assert.Equal("http:502", code);
    }

    [Fact]
    public void ParseConnectError_EmptyBody_FallsBack_To_HttpStatus()
    {
        using var resp = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
        var (code, _) = InvokeParseConnectError([], resp);
        Assert.Equal("http:403", code);
    }

    [Fact]
    public void ParseConnectError_NoReasonPhrase_UsesStatusCodeAsMessage()
    {
        using var resp = new HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
        {
            ReasonPhrase = null,
        };
        var (_, message) = InvokeParseConnectError([], resp);
        Assert.Equal("Conflict", message);
    }

    private static (string Code, string Message) InvokeParseConnectError(byte[] body, HttpResponseMessage resp)
    {
        var mi = s_connectInvokerType.GetMethod(
            "ParseConnectError", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = mi.Invoke(null, [body, resp])!;
        var tuple = (ITuple)result;
        return ((string)tuple[0]!, (string)tuple[1]!);
    }

    // ---- ConnectInvoker.ParseEndOfStreamError (internal static) ------

    [Fact]
    public void ParseEndOfStreamError_EmptyPayload_Returns_NullPair()
    {
        var (code, msg) = InvokeParseEndOfStreamError([]);
        Assert.Null(code);
        Assert.Null(msg);
    }

    [Fact]
    public void ParseEndOfStreamError_NoErrorBlock_Returns_NullPair()
    {
        var (code, msg) = InvokeParseEndOfStreamError(
            Encoding.UTF8.GetBytes("""{"metadata":{}}"""));
        Assert.Null(code);
        Assert.Null(msg);
    }

    [Fact]
    public void ParseEndOfStreamError_WithError_ReturnsConnectCode()
    {
        var (code, msg) = InvokeParseEndOfStreamError(
            Encoding.UTF8.GetBytes("""{"error":{"code":"internal","message":"boom"}}"""));
        Assert.Equal("connect:internal", code);
        Assert.Equal("boom", msg);
    }

    [Fact]
    public void ParseEndOfStreamError_ErrorWithoutCode_Returns_NullCode()
    {
        var (code, msg) = InvokeParseEndOfStreamError(
            Encoding.UTF8.GetBytes("""{"error":{"message":"orphan"}}"""));
        Assert.Null(code);
        Assert.Equal("orphan", msg);
    }

    [Fact]
    public void ParseEndOfStreamError_NonObjectRoot_Returns_NullPair()
    {
        var (code, msg) = InvokeParseEndOfStreamError(
            Encoding.UTF8.GetBytes("[1,2,3]"));
        Assert.Null(code);
        Assert.Null(msg);
    }

    [Fact]
    public void ParseEndOfStreamError_MalformedJson_Returns_NullPair()
    {
        var (code, msg) = InvokeParseEndOfStreamError(
            Encoding.UTF8.GetBytes("{not json"));
        Assert.Null(code);
        Assert.Null(msg);
    }

    [Fact]
    public void ParseEndOfStreamError_NonObjectErrorBlock_Returns_NullPair()
    {
        var (code, msg) = InvokeParseEndOfStreamError(
            Encoding.UTF8.GetBytes("""{"error":"flat string"}"""));
        Assert.Null(code);
        Assert.Null(msg);
    }

    private static (string? Code, string? Message) InvokeParseEndOfStreamError(byte[] payload)
    {
        var mi = s_connectInvokerType.GetMethod(
            "ParseEndOfStreamError", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = mi.Invoke(null, [payload])!;
        var tuple = (ITuple)result;
        return ((string?)tuple[0], (string?)tuple[1]);
    }

    // ---- ProtobufRecordingBuilder error branches ---------------------

    [Fact]
    public async Task LoadAsync_MissingFile_ThrowsFileNotFound()
    {
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            ProtobufRecordingBuilder.LoadAsync(
                "C:/nonexistent/path/no-such-file.pb",
                TestContext.Current.CancellationToken));
        Assert.Contains("gRPC schema file not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_NullPath_ThrowsArgumentException()
    {
        // ArgumentException.ThrowIfNullOrEmpty catches both null and "".
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ProtobufRecordingBuilder.LoadAsync("", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_NonProtobufFile_ThrowsInvalidData()
    {
        var temp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(temp, "this is not a protobuf descriptor set",
                TestContext.Current.CancellationToken);
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProtobufRecordingBuilder.LoadAsync(temp, TestContext.Current.CancellationToken));
            Assert.Contains("FileDescriptorSet", ex.Message, StringComparison.Ordinal);
            Assert.Contains("protoc --descriptor_set_out", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Build_NullDescriptors_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ProtobufRecordingBuilder.Build(null!, null, "test"));
    }

    [Fact]
    public void Build_EmptyDescriptorList_ThrowsBecauseNoSteps()
    {
        // No descriptors → no methods → step count is 0 → the builder
        // raises InvalidDataException so the operator sees the schema
        // had no service blocks.
        var ex = Assert.Throws<InvalidDataException>(() =>
            ProtobufRecordingBuilder.Build([], null, "empty.pb"));
        Assert.Contains("no service methods", ex.Message, StringComparison.Ordinal);
        Assert.Contains("empty.pb", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DescriptorWithoutServices_ThrowsInvalidData()
    {
        // Build a FileDescriptor with messages but no services so the
        // builder hits the "no steps" guard.
        var fileProto = new FileDescriptorProto
        {
            Name = "empty.proto",
            Syntax = "proto3",
            Package = "test",
        };
        var msg = new DescriptorProto { Name = "M" };
        msg.Field.Add(new FieldDescriptorProto
        {
            Name = "x",
            Number = 1,
            Type = FieldDescriptorProto.Types.Type.String,
            Label = FieldDescriptorProto.Types.Label.Optional,
        });
        fileProto.MessageType.Add(msg);

        var descriptors = FileDescriptor.BuildFromByteStrings([fileProto.ToByteString()]);
        var ex = Assert.Throws<InvalidDataException>(() =>
            ProtobufRecordingBuilder.Build(descriptors, [fileProto], "no-services.pb"));
        Assert.Contains("no service methods", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithUnaryAndServerStreamingMethods_ProducesBothShapes()
    {
        // Construct a single .proto with two services: one unary +
        // one server-streaming. Verify the recording carries both
        // shapes (Unary vs ServerStreaming) and that the SS step has
        // exactly three ReceivedMessages frames.
        var fileProto = BuildSampleFileProto();
        var descriptors = FileDescriptor.BuildFromByteStrings([fileProto.ToByteString()]);

        var recording = ProtobufRecordingBuilder.Build(descriptors, [fileProto], "sample.pb");
        Assert.Equal(2, recording.Steps.Count);

        var unary = recording.Steps.Single(s => s.MethodType == "Unary");
        var streaming = recording.Steps.Single(s => s.MethodType == "ServerStreaming");

        Assert.Equal("OK", unary.Status);
        Assert.False(string.IsNullOrEmpty(unary.ResponseBinary));
        Assert.Null(unary.ReceivedMessages);

        Assert.Null(streaming.ResponseBinary);
        Assert.NotNull(streaming.ReceivedMessages);
        Assert.Equal(3, streaming.ReceivedMessages!.Count);
        Assert.Equal(0, streaming.ReceivedMessages[0].TimestampMs);
        Assert.Equal(100, streaming.ReceivedMessages[1].TimestampMs);
        Assert.Equal(200, streaming.ReceivedMessages[2].TimestampMs);
    }

    [Fact]
    public void Build_SchemaDescriptor_Encoded_When_FileProtos_Supplied()
    {
        var fileProto = BuildSampleFileProto();
        var descriptors = FileDescriptor.BuildFromByteStrings([fileProto.ToByteString()]);

        var recording = ProtobufRecordingBuilder.Build(descriptors, [fileProto], "sample.pb");
        Assert.All(recording.Steps, s => Assert.False(string.IsNullOrEmpty(s.SchemaDescriptor)));

        // The base64-decoded form must parse as a FileDescriptorSet
        // containing the original file.
        var blob = recording.Steps[0].SchemaDescriptor!;
        var bytes = Convert.FromBase64String(blob);
        var set = FileDescriptorSet.Parser.ParseFrom(bytes);
        Assert.Single(set.File);
        Assert.Equal("sample.proto", set.File[0].Name);
    }

    [Fact]
    public void Build_NullFileProtos_LeavesSchemaDescriptorNull()
    {
        // When the caller doesn't pass the FileDescriptorProto list,
        // the schemaDescriptor must stay null — gRPC reflection can't
        // be wired up but the step list still builds.
        var fileProto = BuildSampleFileProto();
        var descriptors = FileDescriptor.BuildFromByteStrings([fileProto.ToByteString()]);

        var recording = ProtobufRecordingBuilder.Build(descriptors, fileProtos: null, "no-schema.pb");
        Assert.All(recording.Steps, s => Assert.Null(s.SchemaDescriptor));
    }

    private static FileDescriptorProto BuildSampleFileProto()
    {
        // A minimal proto: package + message Echo + service Greeter
        // with one unary method and one server-streaming method.
        var fileProto = new FileDescriptorProto
        {
            Name = "sample.proto",
            Syntax = "proto3",
            Package = "sample",
        };
        var msg = new DescriptorProto { Name = "Echo" };
        msg.Field.Add(new FieldDescriptorProto
        {
            Name = "text",
            Number = 1,
            Type = FieldDescriptorProto.Types.Type.String,
            Label = FieldDescriptorProto.Types.Label.Optional,
        });
        fileProto.MessageType.Add(msg);

        var service = new ServiceDescriptorProto { Name = "Greeter" };
        service.Method.Add(new MethodDescriptorProto
        {
            Name = "SayHello",
            InputType = ".sample.Echo",
            OutputType = ".sample.Echo",
        });
        service.Method.Add(new MethodDescriptorProto
        {
            Name = "Tick",
            InputType = ".sample.Echo",
            OutputType = ".sample.Echo",
            ServerStreaming = true,
        });
        fileProto.Service.Add(service);
        return fileProto;
    }
}
