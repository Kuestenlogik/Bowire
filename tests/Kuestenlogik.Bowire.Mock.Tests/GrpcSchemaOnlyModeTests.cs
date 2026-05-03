// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Reflection.V1Alpha;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Phase 3d extension: gRPC schema-only mode. Given a protobuf
/// FileDescriptorSet binary, the mock synthesises a BowireRecording
/// where every method gets a sample response encoded by
/// ProtobufSampleEncoder, and serves the lot over gRPC with
/// Reflection enabled.
/// </summary>
public sealed class GrpcSchemaOnlyModeTests : IDisposable
{
    private readonly string _tempDir;

    public GrpcSchemaOnlyModeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-grpc-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Reflection_ListServices_ReturnsSchemaServices()
    {
        // Build a minimal FileDescriptorSet with one service + one
        // method in-memory. Write it to disk, start the mock, then
        // verify the reflection service lists our service.
        var fdsPath = WriteSyntheticFdSet("greeter.pb");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { GrpcSchemaPath = fdsPath, Port = 0, Watch = false, ReplaySpeed = 0, SchemaSources = new IBowireMockSchemaSource[] { new ProtobufMockSchemaSource() }, HostingExtensions = new IBowireMockHostingExtension[] { new GrpcMockHostingExtension() } },
            TestContext.Current.CancellationToken);

        using var channel = GrpcChannel.ForAddress(
            $"http://127.0.0.1:{server.Port}",
            new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler() });

        var reflection = new ServerReflection.ServerReflectionClient(channel);
        using var call = reflection.ServerReflectionInfo(cancellationToken: TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new ServerReflectionRequest { ListServices = "" }, TestContext.Current.CancellationToken);
        await call.RequestStream.CompleteAsync();

        await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken);
        var response = call.ResponseStream.Current;

        var services = response.ListServicesResponse.Service
            .Select(s => s.Name)
            .ToList();
        Assert.Contains("demo.Greeter", services);
    }

    [Fact]
    public async Task UnaryCall_ResponseBytesMatch_SampleEncoding()
    {
        // Invoke the synthetic Greeter.SayHello via GrpcChannel and
        // check the response bytes deterministically: for `HelloReply
        // { string message = 1 }` the encoder emits `message =
        // "sample"`, which on the wire is the 8 bytes
        //   0A 06 73 61 6D 70 6C 65
        //    │  │  └─ "sample"
        //    │  └─ length 6
        //    └─ tag for field 1, length-delimited
        //
        // A pass-through Marshaller<byte[]> keeps both sides free of
        // any codegen requirement — we don't have generated message
        // classes for this synthetic schema at test time.
        var fdsPath = WriteSyntheticFdSet("greeter-unary.pb");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { GrpcSchemaPath = fdsPath, Port = 0, Watch = false, ReplaySpeed = 0, SchemaSources = new IBowireMockSchemaSource[] { new ProtobufMockSchemaSource() }, HostingExtensions = new IBowireMockHostingExtension[] { new GrpcMockHostingExtension() } },
            TestContext.Current.CancellationToken);

        using var handler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true };
        using var channel = GrpcChannel.ForAddress(
            $"http://127.0.0.1:{server.Port}",
            new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = false });

        var passthrough = Marshallers.Create(
            serializer: (byte[] b) => b,
            deserializer: (byte[] b) => b);
        var method = new Method<byte[], byte[]>(
            type: MethodType.Unary,
            serviceName: "demo.Greeter",
            name: "SayHello",
            requestMarshaller: passthrough,
            responseMarshaller: passthrough);

        var invoker = channel.CreateCallInvoker();
        using var call = invoker.AsyncUnaryCall(
            method, host: null, new CallOptions(), Array.Empty<byte>());
        var responseBytes = await call.ResponseAsync;

        Assert.Equal(8, responseBytes.Length);
        Assert.Equal(0x0A, responseBytes[0]); // field 1, wire type 2
        Assert.Equal(0x06, responseBytes[1]); // length 6
        Assert.Equal("sample", System.Text.Encoding.UTF8.GetString(responseBytes, 2, 6));
    }

    // Build a FileDescriptorSet for a tiny Greeter service with:
    //   rpc SayHello(HelloRequest) returns (HelloReply);
    // HelloRequest  { string name = 1; }
    // HelloReply    { string message = 1; }
    // Avoids calling protoc; returns bytes that parse cleanly.
    private string WriteSyntheticFdSet(string fileName)
    {
        var fd = new FileDescriptorProto
        {
            Name = "demo/greeter.proto",
            Package = "demo",
            Syntax = "proto3"
        };
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "HelloRequest",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "name",
                    Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "name"
                }
            }
        });
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "HelloReply",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "message",
                    Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "message"
                }
            }
        });
        fd.Service.Add(new ServiceDescriptorProto
        {
            Name = "Greeter",
            Method =
            {
                new MethodDescriptorProto
                {
                    Name = "SayHello",
                    InputType = ".demo.HelloRequest",
                    OutputType = ".demo.HelloReply"
                }
            }
        });

        var set = new FileDescriptorSet();
        set.File.Add(fd);

        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, set.ToByteArray());
        return path;
    }

}
