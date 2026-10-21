// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// The JSON Schema <see cref="OpenApiDocumentBuilder"/> writes for one field:
/// a list stays a list and a nested object keeps its shape, so the exported
/// contract describes what the API actually exchanges.
/// </summary>
public sealed class OpenApiDocumentBuilderSchemaTests
{
    [Fact]
    public void Build_writes_repeated_fields_as_arrays_and_message_fields_as_objects()
    {
        var address = new BowireMessageInfo("Address", "Address", new List<BowireFieldInfo>
        {
            new("city", 1, "string", "optional", false, false, null, null),
            new("zip", 2, "int32", "optional", false, false, null, null),
        });
        var node = new BowireMessageInfo("Node", "Node", []);
        node.Fields.Add(new BowireFieldInfo("children", 1, "message", "optional", false, true, node, null));
        var output = new BowireMessageInfo("Out", "Out", new List<BowireFieldInfo>
        {
            new("tags", 1, "string", "optional", false, true, null, null),
            new("address", 2, "message", "optional", false, false, address, null),
            new("history", 3, "message", "optional", false, true, address, null),
            new("tree", 4, "message", "optional", false, false, node, null),
            new("blob", 5, "message", "optional", false, false, null, null),
        });
        var input = new BowireMessageInfo("In", "In", new List<BowireFieldInfo>
        {
            new("ids", 1, "int64", "optional", false, true, null, null) { Source = "query" },
        });
        var method = new BowireMethodInfo(
            Name: "get", FullName: "GET /things",
            ClientStreaming: false, ServerStreaming: false,
            InputType: input, OutputType: output,
            MethodType: "Unary") { HttpMethod = "GET", HttpPath = "/things" };
        var svc = new BowireServiceInfo("Things", "rest", new List<BowireMethodInfo> { method });

        var json = OpenApiDocumentBuilder.Build("http://h", new[] { svc }, options:
            new OpenApiExportOptions { Format = OpenApiExportFormat.Json });
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var op = doc.RootElement.GetProperty("paths").GetProperty("/things").GetProperty("get");
        var props = op.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("properties");

        var tags = props.GetProperty("tags");
        Assert.Equal("array", tags.GetProperty("type").GetString());
        Assert.Equal("string", tags.GetProperty("items").GetProperty("type").GetString());

        var addr = props.GetProperty("address");
        Assert.Equal("object", addr.GetProperty("type").GetString());
        Assert.Equal("integer", addr.GetProperty("properties").GetProperty("zip").GetProperty("type").GetString());

        var history = props.GetProperty("history");
        Assert.Equal("array", history.GetProperty("type").GetString());
        Assert.Equal("string", history.GetProperty("items").GetProperty("properties")
            .GetProperty("city").GetProperty("type").GetString());

        // A self-referencing message stops at the cycle instead of recursing.
        var children = props.GetProperty("tree").GetProperty("properties").GetProperty("children");
        Assert.Equal("array", children.GetProperty("type").GetString());
        Assert.Equal("object", children.GetProperty("items").GetProperty("type").GetString());

        Assert.Equal("object", props.GetProperty("blob").GetProperty("type").GetString());

        var ids = op.GetProperty("parameters").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "ids");
        Assert.Equal("array", ids.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("integer", ids.GetProperty("schema").GetProperty("items").GetProperty("type").GetString());
    }
}
