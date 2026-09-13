// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Kuestenlogik.Bowire.SchemaDesigner.Tests;

/// <summary>
/// #247 — the descriptor-set → graph projection.
/// </summary>
/// <remarks>
/// The first test is the reason the rail exists. Discovery's own
/// <c>BowireMessageInfo</c> tree carries a visited-set that spans the whole
/// tree and is never unwound, so the SECOND reference to a type comes back
/// as an empty stub and the cross-reference is simply absent. These tests
/// pin that the graph, built from the flat descriptor set instead, keeps
/// every reference.
/// </remarks>
public sealed class SchemaGraphBuilderTests
{
    private const string Pkg = "harbor.v1";

    [Fact]
    public void TypeUsedTwice_KeepsBothReferences()
    {
        // Vessel is referenced by PortCall AND by Berth — the shape a flat
        // tree renders as two unrelated duplicates.
        var file = File(
            Message("Vessel", Scalar("imo", 1)),
            Message("PortCall", Reference("vessel", 1, "Vessel")),
            Message("Berth", Reference("occupant", 1, "Vessel")));

        var graph = SchemaGraphBuilder.Build([Set(file)]);

        var intoVessel = graph.Edges
            .Where(e => e.To == $"{Pkg}.Vessel" && e.Kind == SchemaEdgeKind.Field)
            .ToList();

        Assert.Equal(2, intoVessel.Count);
        Assert.Contains(intoVessel, e => e.From == $"{Pkg}.PortCall" && e.Label == "vessel");
        Assert.Contains(intoVessel, e => e.From == $"{Pkg}.Berth" && e.Label == "occupant");
        Assert.Equal(2, Node(graph, $"{Pkg}.Vessel").UsageCount);
    }

    [Fact]
    public void SameTypeTwiceInOneMessage_IsTwoEdges()
    {
        // Both fields resolve to the same type. They are two distinct facts
        // about the schema, so they stay two edges — distinguishable only by
        // their field name, which is why the label is carried.
        var file = File(
            Message("Port", Scalar("code", 1)),
            Message("Leg",
                Reference("origin", 1, "Port"),
                Reference("destination", 2, "Port")));

        var graph = SchemaGraphBuilder.Build([Set(file)]);

        var edges = graph.Edges.Where(e => e.To == $"{Pkg}.Port").ToList();
        Assert.Equal(2, edges.Count);
        Assert.Equal("destination", edges.Select(e => e.Label).Order().First());
        Assert.Equal("origin", edges.Select(e => e.Label).Order().Last());
    }

    [Fact]
    public void RecursiveType_TerminatesAndDrawsSelfEdge()
    {
        var file = File(Message("Node", Reference("parent", 1, "Node")));

        var graph = SchemaGraphBuilder.Build([Set(file)]);

        var self = Assert.Single(graph.Edges);
        Assert.Equal($"{Pkg}.Node", self.From);
        Assert.Equal($"{Pkg}.Node", self.To);
    }

    [Fact]
    public void MapField_PointsAtValueTypeAndHidesTheEntry()
    {
        var berths = Message("Berths", MapField("byCode", 1, "Berths.BerthsEntry"));
        berths.NestedType.Add(MapEntry("BerthsEntry", valueType: $".{Pkg}.Berth"));

        var graph = SchemaGraphBuilder.Build([Set(File(
            Message("Berth", Scalar("name", 1)),
            berths))]);

        var edge = Assert.Single(graph.Edges);
        Assert.Equal($"{Pkg}.Berth", edge.To);
        Assert.True(edge.IsMap);
        Assert.False(edge.IsRepeated);
        // protoc's synthetic entry type is not something the operator wrote.
        Assert.DoesNotContain(graph.Nodes, n => n.Name.EndsWith("Entry", StringComparison.Ordinal));
    }

    [Fact]
    public void MapWithScalarValue_DrawsNoEdge()
    {
        var tags = Message("Tags", MapField("byKey", 1, "Tags.TagsEntry"));
        tags.NestedType.Add(MapEntry("TagsEntry", valueType: null));

        var graph = SchemaGraphBuilder.Build([Set(File(tags))]);

        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void RepeatedMessageField_IsMarkedRepeated()
    {
        var file = File(
            Message("Crew", Scalar("name", 1)),
            Message("Vessel", Repeated("crew", 1, "Crew")));

        var graph = SchemaGraphBuilder.Build([Set(file)]);

        var edge = Assert.Single(graph.Edges);
        Assert.True(edge.IsRepeated);
        Assert.False(edge.IsMap);
    }

    [Fact]
    public void NestedTypesAndEnums_BecomeTheirOwnNodes()
    {
        var outer = Message("PortCall", Reference("status", 1, "PortCall.Status"));
        outer.EnumType.Add(new EnumDescriptorProto
        {
            Name = "Status",
            Value = { new EnumValueDescriptorProto { Name = "UNKNOWN", Number = 0 } },
        });
        outer.NestedType.Add(Message("Window", Scalar("from", 1)));

        var graph = SchemaGraphBuilder.Build([Set(File(outer))]);

        var status = Node(graph, $"{Pkg}.PortCall.Status");
        Assert.Equal(SchemaNodeKind.Enum, status.Kind);
        Assert.Equal("Status", status.Name);
        Assert.True(status.IsNested);

        var window = Node(graph, $"{Pkg}.PortCall.Window");
        Assert.Equal(SchemaNodeKind.Message, window.Kind);
        Assert.True(window.IsNested);

        // A field naming an enum is a reference like any other.
        Assert.Contains(graph.Edges, e => e.To == $"{Pkg}.PortCall.Status" && e.Label == "status");
    }

    [Fact]
    public void Methods_BecomeNodesLinkedToRequestAndResponse()
    {
        var file = File(
            Message("GetPortCallRequest", Scalar("id", 1)),
            Message("PortCall", Scalar("id", 1)));
        file.Service.Add(new ServiceDescriptorProto
        {
            Name = "PortCallService",
            Method =
            {
                new MethodDescriptorProto
                {
                    Name = "GetPortCall",
                    InputType = $".{Pkg}.GetPortCallRequest",
                    OutputType = $".{Pkg}.PortCall",
                },
            },
        });

        var graph = SchemaGraphBuilder.Build([Set(file)]);

        var id = $"{Pkg}.PortCallService/GetPortCall";
        var method = Node(graph, id);
        Assert.Equal(SchemaNodeKind.Method, method.Kind);
        Assert.Equal("GetPortCall", method.Name);
        Assert.Equal($"{Pkg}.PortCallService", method.Service);

        Assert.Contains(graph.Edges, e =>
            e.From == id && e.To == $"{Pkg}.GetPortCallRequest" && e.Kind == SchemaEdgeKind.Request);
        Assert.Contains(graph.Edges, e =>
            e.From == id && e.To == $"{Pkg}.PortCall" && e.Kind == SchemaEdgeKind.Response);
    }

    [Fact]
    public void WellKnownTypes_AreLeftOutByDefaultAndTakeTheirEdgesWithThem()
    {
        var wellKnown = new FileDescriptorProto
        {
            Name = "google/protobuf/timestamp.proto",
            Package = "google.protobuf",
            MessageType = { Message("Timestamp", Scalar("seconds", 1)) },
        };
        var domain = File(Message("PortCall",
            new FieldDescriptorProto
            {
                Name = "arrivedAt",
                Number = 1,
                Type = FieldDescriptorProto.Types.Type.Message,
                TypeName = ".google.protobuf.Timestamp",
                Label = FieldDescriptorProto.Types.Label.Optional,
            }));

        var without = SchemaGraphBuilder.Build([Set(domain, wellKnown)]);
        Assert.DoesNotContain(without.Nodes, n => n.Id == "google.protobuf.Timestamp");
        // The edge goes with the node — a dangling edge would read as a
        // missing node rather than an excluded one.
        Assert.Empty(without.Edges);
        Assert.Equal("harbor/v1/harbor.proto", Assert.Single(without.Files));

        var with = SchemaGraphBuilder.Build([Set(domain, wellKnown)], includeWellKnown: true);
        Assert.Contains(with.Nodes, n => n.Id == "google.protobuf.Timestamp");
        Assert.Single(with.Edges);
    }

    [Fact]
    public void SameFileInSeveralDescriptorSets_IsMergedOnce()
    {
        // Each discovered service ships its own transitive dependency
        // closure, so overlapping files are the normal case, not the edge one.
        var file = File(Message("Vessel", Scalar("imo", 1)));

        var graph = SchemaGraphBuilder.Build([Set(file), Set(file), Set(file)]);

        Assert.Single(graph.Nodes);
        Assert.Single(graph.Files);
    }

    [Fact]
    public void UnparseableDescriptor_IsSkippedWithoutLosingTheRest()
    {
        var good = Set(File(Message("Vessel", Scalar("imo", 1))));

        var graph = SchemaGraphBuilder.Build([[0xFF, 0xFE, 0xFD, 0xFC], good]);

        Assert.Single(graph.Nodes);
    }

    [Fact]
    public void NoDescriptors_YieldsTheEmptyGraph()
    {
        Assert.Same(SchemaGraph.Empty, SchemaGraphBuilder.Build([]));
        Assert.Same(SchemaGraph.Empty, SchemaGraphBuilder.Build([[]]));
    }

    [Fact]
    public void FieldCountAndFileName_TravelWithTheNode()
    {
        var graph = SchemaGraphBuilder.Build([Set(File(
            Message("Vessel", Scalar("imo", 1), Scalar("name", 2), Scalar("flag", 3))))]);

        var vessel = Node(graph, $"{Pkg}.Vessel");
        Assert.Equal(3, vessel.FieldCount);
        Assert.Equal("harbor/v1/harbor.proto", vessel.File);
        Assert.False(vessel.IsNested);
    }

    [Fact]
    public void FieldList_CoversScalarsTheEdgesCannotShow()
    {
        // A message whose fields are all scalar contributes no edges at all,
        // so without the field list its detail panel would be blank.
        var graph = SchemaGraphBuilder.Build([Set(File(
            Message("Vessel", Scalar("imo", 1), Scalar("name", 2))))]);

        var vessel = Node(graph, $"{Pkg}.Vessel");
        Assert.Empty(graph.Edges);
        Assert.Equal(["imo", "name"], vessel.Fields.Select(f => f.Name).ToArray());
        Assert.All(vessel.Fields, f => Assert.Equal("string", f.Type));
    }

    [Fact]
    public void FieldList_NamesReferencedTypesMapsAndRepeats()
    {
        var berths = Message("Port", MapField("byCode", 1, "Port.BerthsEntry"));
        berths.NestedType.Add(MapEntry("BerthsEntry", valueType: $".{Pkg}.Berth"));
        berths.Field.Add(Repeated("calls", 2, "PortCall"));
        berths.Field.Add(Reference("primary", 3, "Berth"));
        berths.Field.Add(Scalar("code", 4));

        var graph = SchemaGraphBuilder.Build([Set(File(
            Message("Berth", Scalar("name", 1)),
            Message("PortCall", Scalar("id", 1)),
            berths))]);

        var port = Node(graph, $"{Pkg}.Port");
        var byCode = port.Fields.First(f => f.Name == "byCode");
        Assert.True(byCode.IsMap);
        Assert.False(byCode.IsRepeated);
        // The map names its value type, not protoc's synthetic entry.
        Assert.Equal($"{Pkg}.Berth", byCode.Type);

        var calls = port.Fields.First(f => f.Name == "calls");
        Assert.True(calls.IsRepeated);
        Assert.Equal($"{Pkg}.PortCall", calls.Type);

        Assert.Equal($"{Pkg}.Berth", port.Fields.First(f => f.Name == "primary").Type);
        Assert.Equal("string", port.Fields.First(f => f.Name == "code").Type);
    }

    [Fact]
    public void FieldList_IsEmptyForEnumsAndMethods()
    {
        var file = File(Message("GetRequest", Scalar("id", 1)), Message("GetReply", Scalar("ok", 1)));
        file.EnumType.Add(new EnumDescriptorProto
        {
            Name = "Status",
            Value = { new EnumValueDescriptorProto { Name = "UNKNOWN", Number = 0 } },
        });
        file.Service.Add(new ServiceDescriptorProto
        {
            Name = "Svc",
            Method = { new MethodDescriptorProto { Name = "Get", InputType = $".{Pkg}.GetRequest", OutputType = $".{Pkg}.GetReply" } },
        });

        var graph = SchemaGraphBuilder.Build([Set(file)]);

        Assert.Empty(Node(graph, $"{Pkg}.Status").Fields);
        Assert.Empty(Node(graph, $"{Pkg}.Svc/Get").Fields);
    }

    // ---- descriptor construction helpers ----

    private static SchemaNode Node(SchemaGraph graph, string id) =>
        Assert.Single(graph.Nodes, n => n.Id == id);

    private static byte[] Set(params FileDescriptorProto[] files)
    {
        var set = new FileDescriptorSet();
        set.File.AddRange(files);
        return set.ToByteArray();
    }

    private static FileDescriptorProto File(params DescriptorProto[] messages)
    {
        var file = new FileDescriptorProto { Name = "harbor/v1/harbor.proto", Package = Pkg };
        file.MessageType.AddRange(messages);
        return file;
    }

    private static DescriptorProto Message(string name, params FieldDescriptorProto[] fields)
    {
        var message = new DescriptorProto { Name = name };
        message.Field.AddRange(fields);
        return message;
    }

    private static FieldDescriptorProto Scalar(string name, int number) => new()
    {
        Name = name,
        Number = number,
        Type = FieldDescriptorProto.Types.Type.String,
        Label = FieldDescriptorProto.Types.Label.Optional,
    };

    private static FieldDescriptorProto Reference(string name, int number, string type) => new()
    {
        Name = name,
        Number = number,
        Type = FieldDescriptorProto.Types.Type.Message,
        TypeName = $".{Pkg}.{type}",
        Label = FieldDescriptorProto.Types.Label.Optional,
    };

    private static FieldDescriptorProto Repeated(string name, int number, string type) => new()
    {
        Name = name,
        Number = number,
        Type = FieldDescriptorProto.Types.Type.Message,
        TypeName = $".{Pkg}.{type}",
        Label = FieldDescriptorProto.Types.Label.Repeated,
    };

    /// <summary>A map field as protoc emits it: repeated, naming the synthetic entry type.</summary>
    private static FieldDescriptorProto MapField(string name, int number, string entryType) =>
        Repeated(name, number, entryType);

    /// <summary>
    /// The synthetic entry type protoc generates for a map: key at 1, value
    /// at 2, marked with the <c>map_entry</c> option.
    /// </summary>
    private static DescriptorProto MapEntry(string name, string? valueType)
    {
        var value = valueType is null
            ? new FieldDescriptorProto
            {
                Name = "value",
                Number = 2,
                Type = FieldDescriptorProto.Types.Type.String,
                Label = FieldDescriptorProto.Types.Label.Optional,
            }
            : new FieldDescriptorProto
            {
                Name = "value",
                Number = 2,
                Type = FieldDescriptorProto.Types.Type.Message,
                TypeName = valueType,
                Label = FieldDescriptorProto.Types.Label.Optional,
            };

        return new DescriptorProto
        {
            Name = name,
            Options = new MessageOptions { MapEntry = true },
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "key",
                    Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                },
                value,
            },
        };
    }
}
