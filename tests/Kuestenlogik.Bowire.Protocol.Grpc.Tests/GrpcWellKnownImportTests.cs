// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Google.Protobuf.Reflection;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Xunit;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Tests;

/// <summary>
/// Found by the #177 scaffolder: a service importing
/// <c>google/protobuf/empty.proto</c> could not be called. Reflection ships
/// the imported file along with the service; the batch build then saw it
/// twice (once from the server, once from the seeds) and failed, and the
/// per-file fallback built each file alone first — which always throws on
/// the imports — so it never reached the code that supplies them.
/// </summary>
public sealed class GrpcWellKnownImportTests
{
    private static FileDescriptorProto Proto(FileDescriptor fd) => FileDescriptorProto.Parser.ParseFrom(fd.SerializedData);

    private static FileDescriptorProto Orders(params string[] imports)
    {
        var proto = new FileDescriptorProto { Name = "orders.proto", Package = "orders.v1", Syntax = "proto3" };
        proto.Dependency.AddRange(imports);
        var order = new DescriptorProto { Name = "Order" };
        order.Field.Add(new FieldDescriptorProto { Name = "id", Number = 1, Type = FieldDescriptorProto.Types.Type.String, Label = FieldDescriptorProto.Types.Label.Optional, JsonName = "id" });
        if (imports.Contains("shared.proto"))
        {
            order.Field.Add(new FieldDescriptorProto
            {
                Name = "shared", Number = 2, Type = FieldDescriptorProto.Types.Type.Message,
                TypeName = ".shared.Shared", Label = FieldDescriptorProto.Types.Label.Optional, JsonName = "shared",
            });
        }
        proto.MessageType.Add(order);
        var service = new ServiceDescriptorProto { Name = "Orders" };
        service.Method.Add(new MethodDescriptorProto { Name = "DeleteOrder", InputType = ".orders.v1.Order", OutputType = ".google.protobuf.Empty" });
        proto.Service.Add(service);
        return proto;
    }

    [Fact]
    public void A_service_importing_empty_builds_when_the_server_ships_empty_too()
    {
        var result = GrpcInvoker.BuildFileDescriptorsPublic(
            [Orders("google/protobuf/empty.proto"), Proto(Google.Protobuf.WellKnownTypes.Empty.Descriptor.File)]);

        var orders = Assert.Single(result, fd => fd.Name == "orders.proto");
        var method = orders.Services.Single().Methods.Single();
        Assert.Equal("google.protobuf.Empty", method.OutputType.FullName);
    }

    [Fact]
    public void The_per_file_path_builds_a_file_together_with_its_imports()
    {
        // A third file with an import nobody supplies makes the batch fail,
        // so this exercises the per-file path.
        var shared = new FileDescriptorProto { Name = "shared.proto", Package = "shared", Syntax = "proto3" };
        shared.MessageType.Add(new DescriptorProto { Name = "Shared" });
        var broken = new FileDescriptorProto { Name = "broken.proto", Package = "broken", Syntax = "proto3", Dependency = { "missing.proto" } };

        var result = GrpcInvoker.BuildFileDescriptorsPublic(
            [Orders("shared.proto", "google/protobuf/empty.proto"), shared, Proto(Google.Protobuf.WellKnownTypes.Empty.Descriptor.File), broken]);

        var orders = Assert.Single(result, fd => fd.Name == "orders.proto");
        var field = orders.MessageTypes.Single().FindFieldByName("shared");
        Assert.Equal("shared.Shared", field.MessageType.FullName);
        Assert.Equal("google.protobuf.Empty", orders.Services.Single().Methods.Single().OutputType.FullName);
    }

    [Fact]
    public void Imports_come_before_importers_and_each_file_once()
    {
        var ordered = GrpcInvoker.WithDependencies(
            [Google.Api.AnnotationsReflection.Descriptor, Google.Protobuf.WellKnownTypes.Struct.Descriptor.File, Google.Protobuf.WellKnownTypes.Value.Descriptor.File]);
        var names = ordered.Select(f => f.Name).ToList();
        Assert.Equal(names.Distinct().Count(), names.Count);
        Assert.True(names.IndexOf("google/protobuf/descriptor.proto") < names.IndexOf("google/api/annotations.proto"));
        Assert.True(names.IndexOf("google/api/http.proto") < names.IndexOf("google/api/annotations.proto"));
    }
}
