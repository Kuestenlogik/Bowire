// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Google.Protobuf.Reflection;
using Kuestenlogik.Bowire.IntegrationTests.Services;
using Kuestenlogik.Bowire.Protocol.Grpc;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// A descriptor set uploaded to the workspace, against a server that does not
/// answer reflection (#991).
/// </summary>
/// <remarks>
/// #653 let a call carry its descriptor set in the metadata, which the CLI
/// can do and the workbench cannot: it has no control that sets a marker.
/// What the workbench has is the schema upload, so the set is stored there
/// and the plugin finds it without being told. These tests store a set the
/// way the upload endpoint does and then call with no metadata at all.
/// </remarks>
public sealed class GrpcUploadedDescriptorSetIntegrationTests : IDisposable
{
    private readonly TempUserRoot _storage = new("grpc-uploaded-set");

    public void Dispose()
    {
        _storage.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void UploadGreeterSet()
        => ProtoSetUploadStore.Add(GrpcDescriptorSetIntegrationTests.GreeterDescriptorSet(), "greeter.protoset");

    [Fact]
    public async Task Discovery_Lists_The_Uploaded_Services_Of_A_Server_Without_Reflection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await GrpcDescriptorSetIntegrationTests.StartGreeterWithoutReflectionAsync();
        UploadGreeterSet();

        var registry = new BowireProtocolRegistry();
        registry.Register(new BowireGrpcProtocol());

        var probe = await BowireDiscoveryProbe.RunAsync(
            registry, host.Url, pluginHint: null, showInternalServices: false,
            TimeSpan.FromSeconds(30), ct: ct);

        var greeter = Assert.Single(probe.Services, s => s.Name == "test.Greeter");
        Assert.True(greeter.IsUploaded);
        Assert.Equal(host.Url, greeter.OriginUrl);
        // Real descriptors, not names: the input message has its fields, which
        // is what a .proto upload never delivered.
        var sayHello = Assert.Single(greeter.Methods, m => m.Name == "SayHello");
        Assert.Contains(sayHello.InputType!.Fields, f => f.Name == "name");
    }

    [Fact]
    public async Task A_Unary_Call_Goes_Through_The_Uploaded_Set()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await GrpcDescriptorSetIntegrationTests.StartGreeterWithoutReflectionAsync();
        UploadGreeterSet();

        var result = await new BowireGrpcProtocol().InvokeAsync(
            host.Url, "test.Greeter", "SayHello",
            ["""{"name":"uploaded","count":1}"""],
            showInternalServices: false, metadata: null, ct);

        Assert.Contains("Hello uploaded!", result.Response ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Server_Stream_Goes_Through_The_Uploaded_Set()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await GrpcDescriptorSetIntegrationTests.StartGreeterWithoutReflectionAsync();
        UploadGreeterSet();

        var frames = new List<string>();
        await foreach (var frame in new BowireGrpcProtocol().InvokeStreamAsync(
            host.Url, "test.Greeter", "SayHelloStream",
            ["""{"name":"stream","count":2}"""],
            showInternalServices: false, metadata: null, ct))
        {
            frames.Add(frame);
        }

        Assert.Equal(2, frames.Count);
        Assert.Contains("Hello stream #2", frames[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Duplex_Channel_Opens_Through_The_Uploaded_Set()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await GrpcDescriptorSetIntegrationTests.StartGreeterWithoutReflectionAsync();
        UploadGreeterSet();

        await using var channel = await new BowireGrpcProtocol().OpenChannelAsync(
            host.Url, "test.Greeter", "ChatHellos",
            showInternalServices: false, metadata: null, ct);

        Assert.NotNull(channel);
    }

    [Fact]
    public async Task A_Service_The_Set_Does_Not_Describe_Still_Asks_Its_Server()
    {
        // The set is stored for the workspace, not for one server. A service
        // it says nothing about is the server's to describe, so on this
        // server the answer is the reflection error, not a lookup miss in a
        // set that was never about that service.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await GrpcDescriptorSetIntegrationTests.StartGreeterWithoutReflectionAsync();
        UploadGreeterSet();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            new BowireGrpcProtocol().InvokeAsync(
                host.Url, "other.Service", "Call", ["{}"],
                showInternalServices: false, metadata: null, ct));

        Assert.Contains("Server Reflection", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Set_Handed_To_One_Command_Is_Used_Without_Being_Stored()
    {
        // The command line's shape: --schema api.protoset names the file for
        // this run, and nothing lands in anybody's workspace.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await GrpcDescriptorSetIntegrationTests.StartGreeterWithoutReflectionAsync();

        using (SchemaUploadStore.EnterExplicit([
            new SchemaUploadStore.ExplicitSchema(
                SchemaUploadStore.ProtoSetKind, "", "greeter.protoset",
                GrpcDescriptorSetIntegrationTests.GreeterDescriptorSet()),
        ]))
        {
            var result = await new BowireGrpcProtocol().InvokeAsync(
                host.Url, "test.Greeter", "SayHello",
                ["""{"name":"named","count":1}"""],
                showInternalServices: false, metadata: null, ct);
            Assert.Contains("Hello named!", result.Response ?? "", StringComparison.Ordinal);
        }

        Assert.False(ProtoSetUploadStore.HasUploads);
    }

    [Fact]
    public async Task A_Set_And_A_Proto_For_One_Service_List_It_Once_As_The_Callable_One()
    {
        UploadGreeterSet();
        ProtoUploadStore.AddAndParse(
            """
            syntax = "proto3";
            package test;
            service Greeter { rpc SayHello (HelloRequest) returns (HelloReply); }
            """, "greeter.proto");

        var registry = new BowireProtocolRegistry();
        registry.Register(new BowireGrpcProtocol());
        // No URL: nothing is probed, and what is stored is the whole answer.
        var probe = await BowireDiscoveryProbe.RunAsync(
            registry, "", pluginHint: null, showInternalServices: false,
            TimeSpan.FromSeconds(30), ct: TestContext.Current.CancellationToken);

        var greeter = Assert.Single(probe.Services, s => s.Name == "test.Greeter");
        Assert.Equal("grpc", greeter.Source);
        // All five of the set, not the one the .proto names.
        Assert.Equal(5, greeter.Methods.Count);
    }

    [Fact]
    public void Bytes_That_Are_Not_A_Descriptor_Set_Are_Refused()
    {
        var ex = Assert.Throws<FormatException>(() =>
            new BowireGrpcProtocol().ReadUploadedSchema("syntax = \"proto3\";"u8.ToArray()));

        Assert.Contains("descriptor_set_out", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Set_Compiled_Without_Its_Imports_Is_Refused_And_Names_The_Missing_File()
    {
        // What `protoc --descriptor_set_out` writes without --include_imports:
        // the one file, naming an import nobody supplied.
        var orphan = new FileDescriptorProto
        {
            Name = "orders.proto",
            Package = "shop",
            Syntax = "proto3",
            Dependency = { "shop/money.proto" },
            MessageType =
            {
                new DescriptorProto
                {
                    Name = "Order",
                    Field =
                    {
                        new FieldDescriptorProto
                        {
                            Name = "total", Number = 1,
                            Label = FieldDescriptorProto.Types.Label.Optional,
                            Type = FieldDescriptorProto.Types.Type.Message,
                            TypeName = ".shop.Money",
                        },
                    },
                },
            },
        };
        var set = new FileDescriptorSet { File = { orphan } };

        var ex = Assert.Throws<FormatException>(() =>
            new BowireGrpcProtocol().ReadUploadedSchema(set.ToByteArray()));

        Assert.Contains("shop/money.proto", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--include_imports", ex.Message, StringComparison.Ordinal);
    }
}
