// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.Reflection;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Tests;

/// <summary>
/// #1068 — descriptors from Node's <c>@grpc/reflection</c> (relative type
/// names, no dependencies, <c>google.protobuf.Empty</c> in a file named
/// <c>google_protobuf.proto</c>, proto3 optional without its flag) are
/// repaired before they are built. The fixtures are the bytes a scaffolded
/// Node stub (#754) served.
/// </summary>
public sealed class GrpcDescriptorRepairTests
{
    private static List<FileDescriptorProto> NodeServed() =>
    [
        FileDescriptorProto.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "node-reflection", "orders_v1.pb"))),
        FileDescriptorProto.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "node-reflection", "google_protobuf.pb"))),
    ];

    [Fact]
    public void The_fixture_is_what_made_the_call_fail()
    {
        // Held so the test below cannot pass on bytes that never failed.
        var orders = NodeServed()[0];
        Assert.Empty(orders.Dependency);
        Assert.Contains(orders.MessageType.SelectMany(m => m.Field), f => f.TypeName == "Order");
        Assert.Contains(orders.Service[0].Method, m => m.OutputType == ".google.protobuf.Empty");
    }

    [Fact]
    public void A_Node_servers_descriptors_build_into_its_service()
    {
        var failures = new List<string>();
        var built = GrpcInvoker.BuildFileDescriptors(NodeServed(), failures);

        Assert.Empty(failures);
        var service = Assert.Single(built.SelectMany(f => f.Services), s => s.FullName == "orders.v1.Orders");
        Assert.Equal(["ListOrders", "GetOrder", "CreateOrder", "UpdateOrder", "DeleteOrder"], service.Methods.Select(m => m.Name));
        Assert.Equal("google.protobuf.Empty", service.FindMethodByName("DeleteOrder").OutputType.FullName);
        Assert.Equal("orders.v1.Order", service.FindMethodByName("ListOrders").OutputType.FindFieldByName("orders").MessageType.FullName);

        // proto3 optional: presence, in a synthetic oneof, not a real one.
        var quantity = service.FindMethodByName("CreateOrder").InputType.FindFieldByName("quantity");
        Assert.True(quantity.HasPresence);
        Assert.True(quantity.ContainingOneof?.IsSynthetic);
    }

    [Fact]
    public void What_protoc_writes_comes_through_unchanged()
    {
        var protoc = new List<FileDescriptorProto>
        {
            Google.Protobuf.WellKnownTypes.EmptyReflection.Descriptor.ToProto(),
            Kuestenlogik.Bowire.Protocol.Grpc.Tests.Proto.GapgreeterReflection.Descriptor.ToProto(),
        };

        var repaired = GrpcDescriptorRepair.Repair(protoc);

        Assert.Equal(protoc, repaired);
    }

    [Fact]
    public void A_relative_name_resolves_from_the_innermost_scope_outwards()
    {
        var file = new FileDescriptorProto { Name = "scopes.proto", Package = "a.b", Syntax = "proto3" };
        var outer = new DescriptorProto { Name = "Outer" };
        outer.NestedType.Add(new DescriptorProto { Name = "Inner" });
        outer.Field.Add(new FieldDescriptorProto { Name = "inner", Number = 1, Type = FieldDescriptorProto.Types.Type.Message, TypeName = "Inner" });
        outer.Field.Add(new FieldDescriptorProto { Name = "top", Number = 2, Type = FieldDescriptorProto.Types.Type.Message, TypeName = "Top" });
        file.MessageType.Add(outer);
        file.MessageType.Add(new DescriptorProto { Name = "Top" });

        var repaired = GrpcDescriptorRepair.Repair([file]).Single();

        var fields = repaired.MessageType[0].Field;
        Assert.Equal(".a.b.Outer.Inner", fields[0].TypeName);
        Assert.Equal(".a.b.Top", fields[1].TypeName);
        Assert.Empty(repaired.Dependency);
    }
}
