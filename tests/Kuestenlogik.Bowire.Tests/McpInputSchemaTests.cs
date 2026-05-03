// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.Mcp;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the MCP tool inputSchema → BowireMessageInfo mapping helper.
/// The shape is plain JSON Schema with type=object — Bowire only consumes
/// the subset that maps cleanly to its form-based UI (string / number /
/// boolean / array / nested object), everything else falls back to string.
/// </summary>
public class McpInputSchemaTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void MapInputSchema_HandlesScalarPropertiesAndRequiredList()
    {
        var schema = Parse(/*lang=json,strict*/ """
        {
          "type": "object",
          "properties": {
            "city":  { "type": "string", "description": "City name" },
            "limit": { "type": "integer" },
            "metric": { "type": "boolean" }
          },
          "required": ["city"]
        }
        """);

        var msg = McpDiscoveryClient.MapInputSchema("weather", schema);

        Assert.Equal("weatherInput", msg.Name);
        Assert.Equal(3, msg.Fields.Count);

        var city = msg.Fields.Single(f => f.Name == "city");
        Assert.Equal("string", city.Type);
        Assert.True(city.Required);
        Assert.Equal("City name", city.Description);
        Assert.Equal("body", city.Source);

        var limit = msg.Fields.Single(f => f.Name == "limit");
        Assert.Equal("int64", limit.Type);
        Assert.False(limit.Required);

        var metric = msg.Fields.Single(f => f.Name == "metric");
        Assert.Equal("bool", metric.Type);
    }

    [Fact]
    public void MapInputSchema_HandlesEmptySchemaWithoutThrowing()
    {
        var schema = Parse(/*lang=json,strict*/ """
        { "type": "object" }
        """);

        var msg = McpDiscoveryClient.MapInputSchema("noop", schema);

        Assert.Equal("noopInput", msg.Name);
        Assert.Empty(msg.Fields);
    }

    [Fact]
    public void MapInputSchema_RecursesIntoNestedObjectProperties()
    {
        var schema = Parse(/*lang=json,strict*/ """
        {
          "type": "object",
          "properties": {
            "user": {
              "type": "object",
              "properties": {
                "name":  { "type": "string" },
                "email": { "type": "string" }
              }
            }
          },
          "required": ["user"]
        }
        """);

        var msg = McpDiscoveryClient.MapInputSchema("createUser", schema);
        var user = Assert.Single(msg.Fields);

        Assert.Equal("user", user.Name);
        Assert.Equal("message", user.Type);
        Assert.True(user.Required);
        Assert.NotNull(user.MessageType);
        Assert.Equal(2, user.MessageType!.Fields.Count);
    }

    [Fact]
    public void MapInputSchema_FlatArrayBecomesRepeatedField()
    {
        var schema = Parse(/*lang=json,strict*/ """
        {
          "type": "object",
          "properties": {
            "tags": { "type": "array", "items": { "type": "string" } }
          }
        }
        """);

        var msg = McpDiscoveryClient.MapInputSchema("tagThing", schema);
        var tags = Assert.Single(msg.Fields);

        Assert.Equal("tags", tags.Name);
        Assert.True(tags.IsRepeated);
        Assert.Equal("string", tags.Type);
    }
}
