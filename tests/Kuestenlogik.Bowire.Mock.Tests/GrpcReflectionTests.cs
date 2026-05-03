// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Reflection.V1Alpha;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Verifies the Phase-1c gRPC Server Reflection path — when the recording
/// carries <c>schemaDescriptor</c> bytes, the mock should expose a real
/// reflection endpoint, so a remote client can call
/// <c>grpc.reflection.v1alpha.ServerReflection/ServerReflectionInfo</c>
/// and discover the mocked services without out-of-band access to the
/// original <c>.proto</c> files.
/// </summary>
public sealed class GrpcReflectionTests : IDisposable
{
    static GrpcReflectionTests()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    private readonly string _tempDir;

    public GrpcReflectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-mock-refl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    // Rely on MockServer's Port-0 + MockServer.Port readback — no more
    // flaky port-collision lotteries.

    [Fact]
    public async Task Reflection_FileContainingSymbol_ReturnsFileDescriptorProto()
    {
        // Build a FileDescriptorSet for our dummy service's proto. Reflection
        // indexes files by the services they contain, so the symbol has to
        // live in a proto that declares at least one service — a 'message-
        // only' file like google/protobuf/wrappers.proto wouldn't show up.
        var set = new FileDescriptorSet();
        set.File.Add(BuildDummyService());
        var schemaBase64 = Convert.ToBase64String(set.ToByteArray());

        var recording = new
        {
            id = "rec_refl",
            name = "refl test",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_1",
                    protocol = "grpc",
                    service = "bowire.mock.test.Echoer",
                    method = "Echo",
                    methodType = "Unary",
                    status = "OK",
                    response = "{}",
                    responseBinary = Convert.ToBase64String(Array.Empty<byte>()),
                    schemaDescriptor = schemaBase64
                }
            }
        };
        var path = Path.Combine(_tempDir, "recording.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        // -- Spin up the mock --
        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, HostingExtensions = new IBowireMockHostingExtension[] { new GrpcMockHostingExtension() } },
            TestContext.Current.CancellationToken);

        // -- Call reflection via a typed client against the generated service base --
        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{server.Port}");
        var client = new ServerReflection.ServerReflectionClient(channel);
        using var call = client.ServerReflectionInfo(cancellationToken: TestContext.Current.CancellationToken);

        await call.RequestStream.WriteAsync(new ServerReflectionRequest
        {
            FileContainingSymbol = "bowire.mock.test.Echoer"
        }, TestContext.Current.CancellationToken);
        await call.RequestStream.CompleteAsync();

        Assert.True(await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken));
        var resp = call.ResponseStream.Current;

        Assert.NotNull(resp.FileDescriptorResponse);
        Assert.NotEmpty(resp.FileDescriptorResponse.FileDescriptorProto);

        // Round-trip: parse what the mock returned and confirm it really
        // contains the Echoer service definition.
        var returnedProto = FileDescriptorProto.Parser.ParseFrom(
            resp.FileDescriptorResponse.FileDescriptorProto[0]);
        Assert.Contains(returnedProto.Service, s => s.Name == "Echoer");
    }

    [Fact]
    public async Task Reflection_ListServices_IncludesRecordedService()
    {
        // Same fixture as above; this time ask reflection what services
        // the mock exposes and verify StringValueEcho shows up. Note: the
        // service appears only if the SCHEMA actually declares it, so we
        // build a throwaway proto that does.
        var serviceProto = BuildDummyService();
        var set = new FileDescriptorSet();
        set.File.Add(serviceProto);
        var schemaBase64 = Convert.ToBase64String(set.ToByteArray());

        var recording = new
        {
            id = "rec_refl_list",
            name = "refl list",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_1",
                    protocol = "grpc",
                    service = "bowire.mock.test.Echoer",
                    method = "Echo",
                    methodType = "Unary",
                    status = "OK",
                    response = "{}",
                    responseBinary = Convert.ToBase64String(Array.Empty<byte>()),
                    schemaDescriptor = schemaBase64
                }
            }
        };
        var path = Path.Combine(_tempDir, "recording.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, HostingExtensions = new IBowireMockHostingExtension[] { new GrpcMockHostingExtension() } },
            TestContext.Current.CancellationToken);

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{server.Port}");
        var client = new ServerReflection.ServerReflectionClient(channel);
        using var call = client.ServerReflectionInfo(cancellationToken: TestContext.Current.CancellationToken);

        await call.RequestStream.WriteAsync(new ServerReflectionRequest { ListServices = "" }, TestContext.Current.CancellationToken);
        await call.RequestStream.CompleteAsync();

        Assert.True(await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken));
        var resp = call.ResponseStream.Current;
        Assert.NotNull(resp.ListServicesResponse);

        var serviceNames = resp.ListServicesResponse.Service.Select(s => s.Name).ToList();
        Assert.Contains("bowire.mock.test.Echoer", serviceNames);
    }

    // Build a tiny FileDescriptorProto with a single service so the
    // ListServices test has something to find. google.protobuf.StringValue's
    // proto declares no services — only messages — so we can't reuse it here.
    private static FileDescriptorProto BuildDummyService()
    {
        // Minimal hand-built proto descriptor: one message with one string
        // field, one service with one unary method that uses that message
        // for both input and output.
        var proto = new FileDescriptorProto
        {
            Name = "bowire/mock/test/echoer.proto",
            Syntax = "proto3",
            Package = "bowire.mock.test"
        };
        proto.MessageType.Add(new DescriptorProto
        {
            Name = "EchoMessage",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "value",
                    Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional
                }
            }
        });
        proto.Service.Add(new ServiceDescriptorProto
        {
            Name = "Echoer",
            Method =
            {
                new MethodDescriptorProto
                {
                    Name = "Echo",
                    InputType = ".bowire.mock.test.EchoMessage",
                    OutputType = ".bowire.mock.test.EchoMessage"
                }
            }
        });
        return proto;
    }
}
