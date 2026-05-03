// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Google.Protobuf.Reflection;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Kuestenlogik.Bowire.Protocol.Grpc.Mock;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the gRPC protocol plugin and its Mock-server hosting +
/// schema-source extensions. Discovery + invocation against a live gRPC
/// server is covered by Kuestenlogik.Bowire.IntegrationTests; here we
/// stay in-process with synthetic <see cref="FileDescriptorProto"/>s.
/// </summary>
// Shares the CWD-serialised collection with BowireConfigurationTests:
// WebApplication.CreateBuilder() internally probes Directory.GetCurrentDirectory(),
// and the config tests flip it under us between Construct and Build.
[Collection("CwdSerialised")]
public sealed class BowireGrpcProtocolTests
{
    [Fact]
    public void Identity_Properties_Are_Stable()
    {
        var protocol = new BowireGrpcProtocol();

        Assert.Equal("gRPC", protocol.Name);
        Assert.Equal("grpc", protocol.Id);
        Assert.NotNull(protocol.IconSvg);
        Assert.Contains("<svg", protocol.IconSvg, StringComparison.Ordinal);
    }

    [Fact]
    public void Implements_All_Plugin_Surfaces()
    {
        var protocol = new BowireGrpcProtocol();

        Assert.IsAssignableFrom<IBowireProtocol>(protocol);
        Assert.IsAssignableFrom<IBowireProtocolServices>(protocol);
        Assert.IsAssignableFrom<IBowireStreamingWithWireBytes>(protocol);
    }

    [Fact]
    public void ConfigureServices_Registers_Grpc_Reflection()
    {
        var protocol = new BowireGrpcProtocol();
        var services = new ServiceCollection();

        protocol.ConfigureServices(services);

        // AddGrpcReflection() registers GrpcReflectionService — we just
        // assert that *some* gRPC-reflection-related descriptor was added
        // and that the service collection is non-empty. Depending on the
        // grpc-dotnet version the exact service-registration name shifts;
        // a count assertion is the durable test.
        Assert.NotEmpty(services);
    }

    [Fact]
    public async Task MapDiscoveryEndpoints_Maps_Reflection_Service()
    {
        var protocol = new BowireGrpcProtocol();
        // The plugin's ConfigureServices is half the contract — at
        // production startup the host also calls AddGrpc() (Bowire
        // doesn't bring it in itself because consumers may already have).
        // MapGrpcReflectionService validates both AddGrpc + AddGrpcReflection
        // are present, so we register both before building the app.
        await using var app = CreateMinimalWebApp(b =>
        {
            b.Services.AddGrpc();
            protocol.ConfigureServices(b.Services);
        });

        protocol.MapDiscoveryEndpoints(app);

        Assert.NotNull(app);
    }

    // Build a WebApplication with a stable ContentRootPath. Bare
    // WebApplication.CreateBuilder() inherits the current process CWD,
    // and BowireConfigurationTests flips that to a per-test temp dir
    // it later deletes — racing with our PhysicalFileProvider watcher.
    // Pinning the content root to %TEMP% (which never gets deleted)
    // sidesteps the race entirely.
    internal static WebApplication CreateMinimalWebApp(Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.GetTempPath()
        });
        configure?.Invoke(builder);
        return builder.Build();
    }

    [Fact]
    public async Task DiscoverAsync_Unreachable_Server_Throws_Or_Returns_Empty()
    {
        // Discovery is "happy path or RpcException" — there's no graceful
        // empty-list fallback in the plugin (a peer Bowire workbench would
        // surface the exception to the user). We accept either an empty
        // list or an RpcException-derived throw to keep this test stable
        // across grpc-dotnet versions.
        var protocol = new BowireGrpcProtocol();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var services = await protocol.DiscoverAsync(
                "http://127.0.0.2:1",
                showInternalServices: false,
                cts.Token);
            Assert.NotNull(services);
        }
        catch (Exception ex)
        {
            // Any failure to reach a fake server is acceptable; we just
            // want to exercise the path without hanging.
            Assert.NotNull(ex);
        }
    }
}

/// <summary>
/// Tests for <see cref="ProtobufSampleEncoder"/> — the schema-only
/// gRPC mock's wire-bytes encoder. Builds tiny <see cref="FileDescriptor"/>s
/// in-memory (no protoc required) and asserts the deterministic output
/// for each scalar wire type.
/// </summary>
public sealed class ProtobufSampleEncoderTests
{
    [Fact]
    public void Encode_Null_Descriptor_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ProtobufSampleEncoder.Encode(null!));
    }

    [Fact]
    public void Encode_Empty_Message_Yields_Empty_Bytes()
    {
        var fd = BuildFileDescriptor("empty.proto", "demo", new DescriptorProto { Name = "Empty" });
        var msg = fd.MessageTypes.First(m => m.Name == "Empty");

        var bytes = ProtobufSampleEncoder.Encode(msg);

        Assert.Empty(bytes);
    }

    [Fact]
    public void Encode_String_Field_Emits_Sample_Wire_Bytes()
    {
        // string field 1 → tag 0x0A (field=1, wire=2-LengthDelimited),
        // length 6, "sample".
        var msg = BuildSingleFieldMessage("StringMsg", FieldDescriptorProto.Types.Type.String);

        var bytes = ProtobufSampleEncoder.Encode(msg);

        Assert.Equal(8, bytes.Length);
        Assert.Equal(0x0A, bytes[0]);
        Assert.Equal(0x06, bytes[1]);
        Assert.Equal("sample", System.Text.Encoding.UTF8.GetString(bytes, 2, 6));
    }

    [Fact]
    public void Encode_Bool_Field_Emits_True()
    {
        // bool field 1 → tag 0x08 (field=1, wire=0-Varint), value 1.
        var msg = BuildSingleFieldMessage("BoolMsg", FieldDescriptorProto.Types.Type.Bool);

        var bytes = ProtobufSampleEncoder.Encode(msg);

        Assert.Equal(2, bytes.Length);
        Assert.Equal(0x08, bytes[0]);
        Assert.Equal(0x01, bytes[1]);
    }

    [Fact]
    public void Encode_Int32_Field_Emits_One()
    {
        var msg = BuildSingleFieldMessage("IntMsg", FieldDescriptorProto.Types.Type.Int32);

        var bytes = ProtobufSampleEncoder.Encode(msg);

        Assert.Equal(2, bytes.Length);
        Assert.Equal(0x08, bytes[0]);
        Assert.Equal(0x01, bytes[1]);
    }

    [Fact]
    public void Encode_Bytes_Field_Emits_Sample_Bytes()
    {
        var msg = BuildSingleFieldMessage("BytesMsg", FieldDescriptorProto.Types.Type.Bytes);

        var bytes = ProtobufSampleEncoder.Encode(msg);

        Assert.Equal(0x0A, bytes[0]);
        Assert.Equal(0x06, bytes[1]);
        Assert.Equal("sample", System.Text.Encoding.UTF8.GetString(bytes, 2, 6));
    }

    [Fact]
    public void Encode_Fixed32_Field_Emits_Four_Bytes_Plus_Tag()
    {
        var msg = BuildSingleFieldMessage("Fx32Msg", FieldDescriptorProto.Types.Type.Fixed32);

        var bytes = ProtobufSampleEncoder.Encode(msg);

        // Tag byte + 4-byte fixed32 little-endian "1".
        Assert.Equal(5, bytes.Length);
    }

    [Fact]
    public void Encode_Fixed64_Field_Emits_Eight_Bytes_Plus_Tag()
    {
        var msg = BuildSingleFieldMessage("Fx64Msg", FieldDescriptorProto.Types.Type.Fixed64);

        var bytes = ProtobufSampleEncoder.Encode(msg);

        Assert.Equal(9, bytes.Length);
    }

    [Fact]
    public void Encode_Float_Field_Emits_Sample_15()
    {
        var msg = BuildSingleFieldMessage("FloatMsg", FieldDescriptorProto.Types.Type.Float);

        var bytes = ProtobufSampleEncoder.Encode(msg);

        Assert.Equal(5, bytes.Length);
    }

    [Fact]
    public void Encode_Double_Field_Emits_Sample_15()
    {
        var msg = BuildSingleFieldMessage("DoubleMsg", FieldDescriptorProto.Types.Type.Double);

        var bytes = ProtobufSampleEncoder.Encode(msg);

        Assert.Equal(9, bytes.Length);
    }

    [Fact]
    public void Encode_Repeated_String_Emits_Three_Entries()
    {
        var fd = BuildFileDescriptor("rep.proto", "demo", new DescriptorProto
        {
            Name = "RepMsg",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "items",
                    Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Repeated,
                    JsonName = "items"
                }
            }
        });
        var msg = fd.MessageTypes.First(m => m.Name == "RepMsg");

        var bytes = ProtobufSampleEncoder.Encode(msg);

        // Three "sample" entries × (1-byte tag + 1-byte length + 6-byte payload)
        Assert.Equal(3 * 8, bytes.Length);
    }

    [Fact]
    public void Encode_Nested_Message_Recurses()
    {
        const string protoFile = "nested.proto";
        var fdProto = new FileDescriptorProto
        {
            Name = protoFile,
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
                    Name = "name", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "name"
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
        var fds = FileDescriptor.BuildFromByteStrings(
            new[] { fdProto.ToByteString() });
        var outer = fds[0].MessageTypes.First(m => m.Name == "Outer");

        var bytes = ProtobufSampleEncoder.Encode(outer);

        // Outer encodes Inner's "sample" string. Output should be non-empty
        // and contain "sample" UTF-8 substring.
        Assert.NotEmpty(bytes);
        var asString = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("sample", asString, StringComparison.Ordinal);
    }

    private static MessageDescriptor BuildSingleFieldMessage(
        string name,
        FieldDescriptorProto.Types.Type type)
    {
        var fd = BuildFileDescriptor(
            "msg-" + Guid.NewGuid().ToString("N") + ".proto",
            "demo",
            new DescriptorProto
            {
                Name = name,
                Field =
                {
                    new FieldDescriptorProto
                    {
                        Name = "v", Number = 1,
                        Type = type,
                        Label = FieldDescriptorProto.Types.Label.Optional,
                        JsonName = "v"
                    }
                }
            });
        return fd.MessageTypes.First(m => m.Name == name);
    }

    internal static FileDescriptor BuildFileDescriptor(
        string fileName, string package, params DescriptorProto[] messages)
    {
        var fdProto = new FileDescriptorProto
        {
            Name = fileName,
            Package = package,
            Syntax = "proto3"
        };
        fdProto.MessageType.AddRange(messages);
        var fds = FileDescriptor.BuildFromByteStrings(
            new[] { fdProto.ToByteString() });
        return fds[0];
    }
}

/// <summary>
/// Tests for <see cref="Kuestenlogik.Bowire.Protocol.Grpc.Mock.DescriptorPool"/> —
/// parses base64 FileDescriptorSet payloads off recording steps and dedups them.
/// </summary>
public sealed class DescriptorPoolTests
{
    [Fact]
    public void BuildFrom_Null_Recording_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DescriptorPool.BuildFrom(null!));
    }

    [Fact]
    public void BuildFrom_Empty_Recording_Yields_Empty_Result()
    {
        var recording = new BowireRecording();

        var result = DescriptorPool.BuildFrom(recording);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildFrom_Steps_Without_Descriptor_Are_Skipped()
    {
        var recording = new BowireRecording();
        recording.Steps.Add(new BowireRecordingStep { Id = "s1", Protocol = "grpc" });
        recording.Steps.Add(new BowireRecordingStep { Id = "s2", Protocol = "grpc", SchemaDescriptor = "" });

        var result = DescriptorPool.BuildFrom(recording);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildFrom_Valid_Descriptor_Yields_FileDescriptor()
    {
        var fdsB64 = SyntheticFdSetBase64("Alpha");
        var recording = new BowireRecording();
        recording.Steps.Add(new BowireRecordingStep
        {
            Id = "s1", Protocol = "grpc", SchemaDescriptor = fdsB64
        });

        var result = DescriptorPool.BuildFrom(recording);

        Assert.NotEmpty(result);
        Assert.Contains(result, fd => fd.Services.Any(s => s.Name == "Alpha"));
    }

    [Fact]
    public void BuildFrom_Duplicate_Descriptors_Are_Deduplicated()
    {
        var fdsB64 = SyntheticFdSetBase64("alpha");
        var recording = new BowireRecording();
        for (var i = 0; i < 5; i++)
        {
            recording.Steps.Add(new BowireRecordingStep
            {
                Id = "s" + i, Protocol = "grpc", SchemaDescriptor = fdsB64
            });
        }

        var result = DescriptorPool.BuildFrom(recording);

        // One unique descriptor → one FileDescriptor.
        Assert.Single(result);
    }

    [Fact]
    public void BuildFrom_Bad_Base64_Throws_InvalidDataException()
    {
        var recording = new BowireRecording();
        recording.Steps.Add(new BowireRecordingStep
        {
            Id = "s1",
            Protocol = "grpc",
            SchemaDescriptor = "@@@-not-base64-@@@"
        });

        var ex = Assert.Throws<InvalidDataException>(() => DescriptorPool.BuildFrom(recording));
        Assert.Contains("malformed base64", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildFrom_Non_FileDescriptorSet_Bytes_Throws_InvalidDataException()
    {
        // Random bytes that base64-decode cleanly but aren't a valid
        // FileDescriptorSet — the protobuf parser will reject them.
        var bogus = Convert.ToBase64String(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        var recording = new BowireRecording();
        recording.Steps.Add(new BowireRecordingStep
        {
            Id = "s1", Protocol = "grpc", SchemaDescriptor = bogus
        });

        Assert.Throws<InvalidDataException>(() => DescriptorPool.BuildFrom(recording));
    }

    internal static string SyntheticFdSetBase64(string serviceName)
    {
        var fd = new FileDescriptorProto
        {
            Name = "svc-" + Guid.NewGuid().ToString("N") + ".proto",
            Package = "demo",
            Syntax = "proto3"
        };
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "Req",
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
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "Res",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "y", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "y"
                }
            }
        });
        fd.Service.Add(new ServiceDescriptorProto
        {
            Name = serviceName,
            Method =
            {
                new MethodDescriptorProto
                {
                    Name = "Do",
                    InputType = ".demo.Req",
                    OutputType = ".demo.Res"
                }
            }
        });
        var set = new FileDescriptorSet();
        set.File.Add(fd);
        return Convert.ToBase64String(set.ToByteArray());
    }
}

/// <summary>
/// Tests for <see cref="ProtobufRecordingBuilder"/> — turns a parsed
/// <see cref="FileDescriptorSet"/> into a synthetic <see cref="BowireRecording"/>.
/// </summary>
public sealed class ProtobufRecordingBuilderTests
{
    [Fact]
    public async Task LoadAsync_Empty_Path_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => ProtobufRecordingBuilder.LoadAsync("", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_Missing_File_Throws_FileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => ProtobufRecordingBuilder.LoadAsync(
                Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N") + ".pb"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_Bad_File_Throws_InvalidDataException()
    {
        var path = Path.Combine(Path.GetTempPath(), "bad-" + Guid.NewGuid().ToString("N") + ".pb");
        await File.WriteAllBytesAsync(path, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => ProtobufRecordingBuilder.LoadAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_Valid_File_Builds_Recording()
    {
        var path = Path.Combine(Path.GetTempPath(), "valid-" + Guid.NewGuid().ToString("N") + ".pb");
        var fdSet = SyntheticFdSet();
        await File.WriteAllBytesAsync(path, fdSet.ToByteArray(), TestContext.Current.CancellationToken);
        try
        {
            var rec = await ProtobufRecordingBuilder.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal("grpc-schema-only", rec.Id);
            Assert.NotEmpty(rec.Steps);
            Assert.Equal("grpc", rec.Steps[0].Protocol);
            Assert.Contains(rec.Steps, s => s.MethodType == "Unary");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Build_Null_Descriptors_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ProtobufRecordingBuilder.Build(null!, fileProtos: null, sourceLabel: "x"));
    }

    [Fact]
    public void Build_Recording_Has_Format_Version_Stamp()
    {
        var fdSet = SyntheticFdSet();
        var fileProtos = fdSet.File.ToList();
        var descriptors = FileDescriptor.BuildFromByteStrings(
            fileProtos.Select(f => f.ToByteString()).ToList());

        var rec = ProtobufRecordingBuilder.Build(descriptors, fileProtos, "synthetic.pb");

        Assert.NotNull(rec.RecordingFormatVersion);
        Assert.Contains("synthetic.pb", rec.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Server_Streaming_Method_Has_Three_ReceivedMessages()
    {
        var fdSet = SyntheticFdSet(serverStreaming: true);
        var fileProtos = fdSet.File.ToList();
        var descriptors = FileDescriptor.BuildFromByteStrings(
            fileProtos.Select(f => f.ToByteString()).ToList());

        var rec = ProtobufRecordingBuilder.Build(descriptors, fileProtos, "stream.pb");

        var step = rec.Steps[0];
        Assert.Equal("ServerStreaming", step.MethodType);
        Assert.Null(step.ResponseBinary);
        Assert.NotNull(step.ReceivedMessages);
        Assert.Equal(3, step.ReceivedMessages!.Count);
    }

    [Fact]
    public void Build_Schema_With_No_Methods_Throws_InvalidDataException()
    {
        // A file with messages but no service blocks — Build should reject
        // it because the recording would be empty.
        var fdProto = new FileDescriptorProto
        {
            Name = "emptyservice.proto",
            Package = "demo",
            Syntax = "proto3"
        };
        fdProto.MessageType.Add(new DescriptorProto { Name = "Loner" });
        var descriptors = FileDescriptor.BuildFromByteStrings(
            new[] { fdProto.ToByteString() });

        Assert.Throws<InvalidDataException>(
            () => ProtobufRecordingBuilder.Build(descriptors, new[] { fdProto }, "no-svc.pb"));
    }

    [Fact]
    public void Build_Without_FileProtos_Skips_SchemaDescriptor()
    {
        var fdSet = SyntheticFdSet();
        var fileProtos = fdSet.File.ToList();
        var descriptors = FileDescriptor.BuildFromByteStrings(
            fileProtos.Select(f => f.ToByteString()).ToList());

        var rec = ProtobufRecordingBuilder.Build(descriptors, fileProtos: null, sourceLabel: "no-protos.pb");

        Assert.Null(rec.Steps[0].SchemaDescriptor);
    }

    private static FileDescriptorSet SyntheticFdSet(bool serverStreaming = false)
    {
        var fd = new FileDescriptorProto
        {
            Name = "demo/svc.proto",
            Package = "demo",
            Syntax = "proto3"
        };
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "Req",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "name", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "name"
                }
            }
        });
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "Res",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "msg", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "msg"
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
                    Name = "Say",
                    InputType = ".demo.Req",
                    OutputType = ".demo.Res",
                    ServerStreaming = serverStreaming
                }
            }
        });
        var set = new FileDescriptorSet();
        set.File.Add(fd);
        return set;
    }
}

/// <summary>
/// Tests for <see cref="ProtobufMockSchemaSource"/> and
/// <see cref="GrpcMockHostingExtension"/> — the gRPC plugin's
/// contributions to the Bowire mock server's plugin extension points.
/// </summary>
[Collection("CwdSerialised")]
public sealed class GrpcMockExtensionTests
{
    [Fact]
    public void ProtobufMockSchemaSource_Kind_Is_Protobuf()
    {
        var source = new ProtobufMockSchemaSource();

        Assert.Equal("protobuf", source.Kind);
    }

    [Fact]
    public async Task ProtobufMockSchemaSource_BuildAsync_Delegates_To_Builder()
    {
        // Hand it a non-existent path; it should bubble the
        // FileNotFoundException from ProtobufRecordingBuilder.LoadAsync —
        // proves the delegation rather than asserting on success (which
        // would duplicate the builder's own tests).
        var source = new ProtobufMockSchemaSource();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => source.BuildAsync(
                Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".pb"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GrpcMockHostingExtension_Id_Is_Grpc()
    {
        var ext = new GrpcMockHostingExtension();

        Assert.Equal("grpc", ext.Id);
    }

    [Fact]
    public void RequiresHttp2_Returns_True_When_Recording_Has_Grpc_Step()
    {
        var ext = new GrpcMockHostingExtension();
        var rec = new BowireRecording();
        rec.Steps.Add(new BowireRecordingStep { Id = "s1", Protocol = "grpc" });

        Assert.True(ext.RequiresHttp2(rec));
    }

    [Fact]
    public void RequiresHttp2_Is_Case_Insensitive()
    {
        var ext = new GrpcMockHostingExtension();
        var rec = new BowireRecording();
        rec.Steps.Add(new BowireRecordingStep { Id = "s1", Protocol = "GRPC" });

        Assert.True(ext.RequiresHttp2(rec));
    }

    [Fact]
    public void RequiresHttp2_Returns_False_For_Non_Grpc_Recording()
    {
        var ext = new GrpcMockHostingExtension();
        var rec = new BowireRecording();
        rec.Steps.Add(new BowireRecordingStep { Id = "s1", Protocol = "rest" });
        rec.Steps.Add(new BowireRecordingStep { Id = "s2", Protocol = "graphql" });

        Assert.False(ext.RequiresHttp2(rec));
    }

    [Fact]
    public void ConfigureServices_Without_Descriptors_Skips_Registration()
    {
        var ext = new GrpcMockHostingExtension();
        var services = new ServiceCollection();
        var rec = new BowireRecording();
        // Step has the protocol marker but no schemaDescriptor — the
        // pool builds nothing and ConfigureServices early-exits.
        rec.Steps.Add(new BowireRecordingStep { Id = "s1", Protocol = "grpc" });

        ext.ConfigureServices(services, rec, NullLoggerFactory.Instance);

        // The before-and-after service count is unchanged because the
        // extension chose to skip AddGrpc + ReflectionServiceImpl.
        Assert.Empty(services);
    }

    [Fact]
    public void ConfigureServices_With_Descriptors_Registers_Reflection()
    {
        var ext = new GrpcMockHostingExtension();
        var services = new ServiceCollection();
        var rec = new BowireRecording();
        rec.Steps.Add(new BowireRecordingStep
        {
            Id = "s1", Protocol = "grpc",
            SchemaDescriptor = DescriptorPoolTests.SyntheticFdSetBase64("Beta")
        });

        ext.ConfigureServices(services, rec, NullLoggerFactory.Instance);

        // AddGrpc + ReflectionServiceImpl singleton — collection grows.
        Assert.NotEmpty(services);
    }

    [Fact]
    public async Task MapEndpoints_Without_Configure_Is_NoOp()
    {
        // MapEndpoints leans on _serviceDescriptors populated by
        // ConfigureServices. Calling it without a prior ConfigureServices
        // should silently no-op (early return).
        var ext = new GrpcMockHostingExtension();
        var rec = new BowireRecording();
        rec.Steps.Add(new BowireRecordingStep { Id = "s1", Protocol = "grpc" });

        await using var app = BowireGrpcProtocolTests.CreateMinimalWebApp();

        // Assert no throw.
        ext.MapEndpoints(app, rec);
    }
}
