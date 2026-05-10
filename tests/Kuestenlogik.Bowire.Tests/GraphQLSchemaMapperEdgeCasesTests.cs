// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.GraphQL;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Edge-case + defensive-fallback coverage for
/// <c>GraphQLSchemaMapper</c>. The happy paths live in
/// <see cref="GraphQLSchemaMapperTests"/>; this file walks the early-
/// exit branches that fire on malformed / partial introspection
/// payloads so the mapper degrades gracefully rather than throwing.
/// </summary>
public sealed class GraphQLSchemaMapperEdgeCasesTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Map_NoSchemaProperty_ReturnsEmpty()
    {
        var schema = Parse("""{ "noSchema": true }""");

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        Assert.Empty(services);
    }

    [Fact]
    public void Map_NullRootRef_SkipsService()
    {
        // queryType is `null` → not an object → AddRootService returns early.
        var schema = Parse("""
            { "__schema": { "queryType": null, "mutationType": null, "subscriptionType": null, "types": [] } }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        Assert.Empty(services);
    }

    [Fact]
    public void Map_RootRefMissingName_SkipsService()
    {
        // Object without a name property — line 59 guard.
        var schema = Parse("""
            {
              "__schema": {
                "queryType": { },
                "mutationType": null,
                "subscriptionType": null,
                "types": []
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        Assert.Empty(services);
    }

    [Fact]
    public void Map_RootNameNotInTypesIndex_SkipsService()
    {
        // queryType.name "Query" but no type with that name in `types`.
        var schema = Parse("""
            {
              "__schema": {
                "queryType": { "name": "Query" },
                "mutationType": null,
                "subscriptionType": null,
                "types": []
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        Assert.Empty(services);
    }

    [Fact]
    public void Map_RootTypeWithEmptyFieldsArray_SkipsService()
    {
        // A schema can declare `type Query` with zero fields; we don't
        // emit a service for that case (line 75 guard) because it would
        // surface a method-less tab in the UI.
        var schemaEmpty = Parse("""
            {
              "__schema": {
                "queryType": { "name": "Query" },
                "mutationType": null,
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "name": "Query", "fields": [], "inputFields": null, "enumValues": null }
                ]
              }
            }
            """);

        Assert.Empty(new GraphQLSchemaMapper().Map(schemaEmpty, "http://localhost/graphql"));
    }

    [Fact]
    public void Map_RootTypeWithoutFieldsArray_SkipsService()
    {
        var schema = Parse("""
            {
              "__schema": {
                "queryType": { "name": "Query" },
                "mutationType": null,
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "name": "Query", "fields": null, "inputFields": null, "enumValues": null }
                ]
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        Assert.Empty(services);
    }

    [Fact]
    public void Map_TypeWithoutNameProperty_DoesNotIndexButStillReturnsServices()
    {
        // `types` contains an entry without a `name` — the indexer skips
        // it (line 32 guard) but the rest of the mapper carries on.
        var schema = Parse("""
            {
              "__schema": {
                "queryType": { "name": "Query" },
                "mutationType": null,
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "fields": [] },
                  { "kind": "OBJECT", "name": "Query", "fields": [
                    { "name": "ping", "args": [], "type": { "kind": "SCALAR", "name": "String" } }
                  ], "inputFields": null, "enumValues": null }
                ]
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        var svc = Assert.Single(services);
        Assert.Equal("Query", svc.Name);
    }

    [Fact]
    public void Map_FieldWithoutTypeProperty_FallsBackToGraphQLResponseStub()
    {
        // The mapper has a defensive branch for missing `type` on a field
        // that produces the GraphQLResponse placeholder.
        var schema = Parse("""
            {
              "__schema": {
                "queryType": { "name": "Query" },
                "mutationType": null,
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "name": "Query",
                    "fields": [ { "name": "weird", "args": [] } ],
                    "inputFields": null, "enumValues": null }
                ]
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        var output = services.Single().Methods.Single().OutputType;
        Assert.Equal("GraphQLResponse", output.Name);
        Assert.Empty(output.Fields);
    }

    [Theory]
    [InlineData("Int", "int32")]
    [InlineData("Float", "double")]
    [InlineData("Boolean", "bool")]
    [InlineData("ID", "string")]
    [InlineData("String", "string")]
    [InlineData("DateTime", "string")] // unknown scalar → string fallback
    [InlineData(null, "string")]
    public void Map_MapsScalarArgTypes_WithExpectedJsType(string? scalarName, string expectedType)
    {
        var typeJson = scalarName is null
            ? """{ "kind": "SCALAR", "name": null }"""
            : $$"""{ "kind": "SCALAR", "name": "{{scalarName}}" }""";
        var schema = Parse($$"""
            {
              "__schema": {
                "queryType": { "name": "Query" },
                "mutationType": null,
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "name": "Query",
                    "fields": [ {
                      "name": "f",
                      "args": [ { "name": "v", "type": {{typeJson}} } ],
                      "type": { "kind": "SCALAR", "name": "String" }
                    } ],
                    "inputFields": null, "enumValues": null }
                ]
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        var arg = services.Single().Methods.Single().InputType.Fields.Single();
        Assert.Equal(expectedType, arg.Type);
    }

    [Fact]
    public void Map_ObjectArgTypeFallsBackToMessage()
    {
        // OBJECT/INTERFACE/UNION as an argument type isn't valid GraphQL
        // but the mapper still produces a "message" placeholder so the form
        // renders rather than crashing on the unsupported case.
        var schema = Parse("""
            {
              "__schema": {
                "queryType": { "name": "Query" },
                "mutationType": null,
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "name": "Query",
                    "fields": [ {
                      "name": "f",
                      "args": [ { "name": "obj", "type": { "kind": "OBJECT", "name": "Foo" } } ],
                      "type": { "kind": "SCALAR", "name": "String" }
                    } ],
                    "inputFields": null, "enumValues": null }
                ]
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        var arg = services.Single().Methods.Single().InputType.Fields.Single();
        Assert.Equal("message", arg.Type);
        Assert.Null(arg.MessageType);
    }

    [Fact]
    public void Map_UnknownArgKindFallsBackToString()
    {
        // The default switch arm for ResolveType — a kind we don't know
        // (the spec might add new ones) maps to a string scalar so the
        // form still renders a textbox.
        var schema = Parse("""
            {
              "__schema": {
                "queryType": { "name": "Query" },
                "mutationType": null,
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "name": "Query",
                    "fields": [ {
                      "name": "f",
                      "args": [ { "name": "x", "type": { "kind": "FUTURE_KIND" } } ],
                      "type": { "kind": "SCALAR", "name": "String" }
                    } ],
                    "inputFields": null, "enumValues": null }
                ]
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        Assert.Equal("string", services.Single().Methods.Single().InputType.Fields.Single().Type);
    }

    [Fact]
    public void Map_EnumWithoutValuesArray_ReturnsNullEnumValues()
    {
        // Enum reference resolves to a type whose `enumValues` is null —
        // the mapper bails out of ResolveEnumValues with null so the
        // field still renders as a string.
        var schema = Parse("""
            {
              "__schema": {
                "queryType": { "name": "Query" },
                "mutationType": null,
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "name": "Query",
                    "fields": [ { "name": "f",
                      "args": [ { "name": "k", "type": { "kind": "ENUM", "name": "K" } } ],
                      "type": { "kind": "SCALAR", "name": "String" }
                    } ],
                    "inputFields": null, "enumValues": null },
                  { "kind": "ENUM", "name": "K", "fields": null, "inputFields": null, "enumValues": null }
                ]
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        var arg = services.Single().Methods.Single().InputType.Fields.Single();
        Assert.Null(arg.EnumValues);
    }

    [Fact]
    public void Map_EnumWithUnknownReference_ReturnsNullEnumValues()
    {
        // Argument type points at an enum that's not in the schema's
        // types list — defensive guard, mapper returns null.
        var schema = Parse("""
            {
              "__schema": {
                "queryType": { "name": "Query" },
                "mutationType": null,
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "name": "Query",
                    "fields": [ { "name": "f",
                      "args": [ { "name": "k", "type": { "kind": "ENUM", "name": "MissingEnum" } } ],
                      "type": { "kind": "SCALAR", "name": "String" }
                    } ],
                    "inputFields": null, "enumValues": null }
                ]
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        Assert.Null(services.Single().Methods.Single().InputType.Fields.Single().EnumValues);
    }

    [Fact]
    public void Map_OutputType_UnionField_RendersAsMessageWithoutNested()
    {
        // ResolveOutputFieldType's UNION branch returns a "message" with
        // null nested — the picker handles this as a leaf.
        var schema = Parse("""
            {
              "__schema": {
                "queryType": { "name": "Query" },
                "mutationType": null,
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "name": "Query",
                    "fields": [ { "name": "find", "args": [],
                      "type": { "kind": "OBJECT", "name": "Result" } } ],
                    "inputFields": null, "enumValues": null },
                  { "kind": "OBJECT", "name": "Result",
                    "fields": [
                      { "name": "kind", "args": [],
                        "type": { "kind": "UNION", "name": "Either" } } ],
                    "inputFields": null, "enumValues": null }
                ]
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        var output = services.Single().Methods.Single().OutputType;
        var kind = output.Fields.Single();
        Assert.Equal("message", kind.Type);
        Assert.Null(kind.MessageType);
    }

    [Fact]
    public void Map_OutputType_EnumField_RendersAsString()
    {
        var schema = Parse("""
            {
              "__schema": {
                "queryType": { "name": "Query" },
                "mutationType": null,
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "name": "Query",
                    "fields": [ { "name": "get", "args": [],
                      "type": { "kind": "OBJECT", "name": "Item" } } ],
                    "inputFields": null, "enumValues": null },
                  { "kind": "OBJECT", "name": "Item",
                    "fields": [
                      { "name": "status", "args": [],
                        "type": { "kind": "ENUM", "name": "Status" } } ],
                    "inputFields": null, "enumValues": null },
                  { "kind": "ENUM", "name": "Status",
                    "enumValues": [ { "name": "OK" } ] }
                ]
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        var status = services.Single().Methods.Single().OutputType.Fields.Single();
        Assert.Equal("string", status.Type);
    }

    [Fact]
    public void Map_OutputType_UnknownFieldKind_FallsBackToString()
    {
        // Default arm of ResolveOutputFieldType.
        var schema = Parse("""
            {
              "__schema": {
                "queryType": { "name": "Query" },
                "mutationType": null,
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "name": "Query",
                    "fields": [ { "name": "get", "args": [],
                      "type": { "kind": "OBJECT", "name": "Item" } } ],
                    "inputFields": null, "enumValues": null },
                  { "kind": "OBJECT", "name": "Item",
                    "fields": [
                      { "name": "blob", "args": [],
                        "type": { "kind": "FUTURE_KIND" } } ],
                    "inputFields": null, "enumValues": null }
                ]
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        var blob = services.Single().Methods.Single().OutputType.Fields.Single();
        Assert.Equal("string", blob.Type);
    }

    [Fact]
    public void Map_OutputType_ObjectReferenceWithoutDefinition_ReturnsEmptyTree()
    {
        // The Query field's type points at an object that's missing from
        // the types index. ResolveOutputType returns null and the method
        // falls back to the GraphQLResponse placeholder.
        var schema = Parse("""
            {
              "__schema": {
                "queryType": { "name": "Query" },
                "mutationType": null,
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "name": "Query",
                    "fields": [ { "name": "get", "args": [],
                      "type": { "kind": "OBJECT", "name": "GhostType" } } ],
                    "inputFields": null, "enumValues": null }
                ]
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        var output = services.Single().Methods.Single().OutputType;
        Assert.Equal("GraphQLResponse", output.Name);
        Assert.Empty(output.Fields);
    }

    [Fact]
    public void Map_OutputType_ObjectWithoutFieldsArray_ReturnsEmptyTree()
    {
        // The named type exists but its `fields` property is null — the
        // mapper returns a BowireMessageInfo with empty fields rather
        // than crashing on EnumerateArray.
        var schema = Parse("""
            {
              "__schema": {
                "queryType": { "name": "Query" },
                "mutationType": null,
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "name": "Query",
                    "fields": [ { "name": "get", "args": [],
                      "type": { "kind": "OBJECT", "name": "Empty" } } ],
                    "inputFields": null, "enumValues": null },
                  { "kind": "OBJECT", "name": "Empty",
                    "fields": null, "inputFields": null, "enumValues": null }
                ]
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        var output = services.Single().Methods.Single().OutputType;
        Assert.Equal("Empty", output.Name);
        Assert.Empty(output.Fields);
    }

    [Fact]
    public void Map_InputObjectWithoutInputFields_ReturnsEmptyMessage()
    {
        // ResolveInputObject's defensive guard for inputFields == null —
        // returns the type with an empty fields list.
        var schema = Parse("""
            {
              "__schema": {
                "queryType": null,
                "mutationType": { "name": "Mutation" },
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "name": "Mutation",
                    "fields": [ {
                      "name": "save",
                      "args": [ { "name": "input", "type": { "kind": "INPUT_OBJECT", "name": "Empty" } } ],
                      "type": { "kind": "SCALAR", "name": "String" }
                    } ],
                    "inputFields": null, "enumValues": null },
                  { "kind": "INPUT_OBJECT", "name": "Empty",
                    "fields": null, "inputFields": null, "enumValues": null }
                ]
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");
        var input = services.Single().Methods.Single().InputType.Fields.Single();
        Assert.NotNull(input.MessageType);
        Assert.Equal("Empty", input.MessageType!.Name);
        Assert.Empty(input.MessageType.Fields);
    }

    [Fact]
    public void Map_InputObjectMissingFromIndex_ReturnsNullNested()
    {
        // ResolveInputObject early-out when the named type isn't indexed.
        var schema = Parse("""
            {
              "__schema": {
                "queryType": null,
                "mutationType": { "name": "Mutation" },
                "subscriptionType": null,
                "types": [
                  { "kind": "OBJECT", "name": "Mutation",
                    "fields": [ {
                      "name": "save",
                      "args": [ { "name": "input", "type": { "kind": "INPUT_OBJECT", "name": "Ghost" } } ],
                      "type": { "kind": "SCALAR", "name": "String" }
                    } ],
                    "inputFields": null, "enumValues": null }
                ]
              }
            }
            """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");
        var input = services.Single().Methods.Single().InputType.Fields.Single();
        Assert.Equal("message", input.Type);
        Assert.Null(input.MessageType);
    }
}
