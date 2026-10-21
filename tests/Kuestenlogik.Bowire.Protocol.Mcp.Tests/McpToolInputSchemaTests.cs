// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tests;

/// <summary>
/// How a tool's JSON Schema <c>inputSchema</c> becomes the form fields
/// Bowire renders (<see cref="BowireMcpProtocol.MapToolInputSchema"/>).
/// </summary>
public sealed class McpToolInputSchemaTests
{
    private static Models.BowireMessageInfo Map(string schema)
    {
        using var doc = JsonDocument.Parse(schema);
        return BowireMcpProtocol.MapToolInputSchema("t", doc.RootElement.Clone());
    }

    [Fact]
    public void A_Type_Given_As_An_Array_Takes_Its_Non_Null_Member()
    {
        // JSON Schema allows "type" as an array; ["string","null"] is how a
        // nullable string is written, and generators emit it routinely.
        var input = Map("""
            {"type":"object","properties":{
              "name":{"type":["string","null"]},
              "count":{"type":["null","integer"]},
              "tags":{"type":["array","null"],"items":{"type":"string"}}
            }}
            """);

        Assert.Equal("string", input.Fields.Single(f => f.Name == "name").Type);
        Assert.Equal("integer", input.Fields.Single(f => f.Name == "count").Type);
        var tags = input.Fields.Single(f => f.Name == "tags");
        Assert.True(tags.IsRepeated);
        Assert.Equal("string", tags.Type);
    }

    [Fact]
    public void A_Property_Without_A_Usable_Type_Or_Description_Is_A_String_Field()
    {
        var input = Map("""
            {"type":"object","properties":{
              "a":{"type":["null"]},
              "b":{"type":42,"description":{"text":"not a string"}},
              "c":{}
            }}
            """);

        Assert.All(input.Fields, f => Assert.Equal("string", f.Type));
        Assert.Null(input.Fields.Single(f => f.Name == "b").Description);
    }
}
