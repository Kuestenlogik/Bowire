// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Kuestenlogik.Bowire.Protocol.GraphQL.Tests;

/// <summary>
/// An uploaded SDL document read into services (#994).
/// </summary>
public sealed class GraphQLSdlImportTests
{
    private const string HarborSdl = """
        "A vessel in the registry."
        type Ship {
          id: ID!
          name: String!
          flag: String
        }

        input ShipFilter { flag: String }

        type Query {
          ship(id: ID!): Ship
          ships(filter: ShipFilter, first: Int): [Ship!]!
        }

        type Mutation {
          renameShip(id: ID!, name: String!): Ship
        }
        """;

    private static ReadOnlyMemory<byte> Doc(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Read_Returns_The_Root_Operations_As_Introspection_Would()
    {
        var services = GraphQLSdlImport.Read(Doc(HarborSdl));

        Assert.Equal(["Query", "Mutation"], services.Select(s => s.Name));

        var ship = Assert.Single(services[0].Methods, m => m.Name == "ship");
        var id = Assert.Single(ship.InputType.Fields);
        Assert.Equal("id", id.Name);
        Assert.True(id.Required);
        // The output type is walked, which is what lets a call select real
        // fields instead of __typename.
        Assert.Contains(ship.OutputType.Fields, f => f.Name == "name");

        Assert.Equal(["renameShip"], services[1].Methods.Select(m => m.Name));
    }

    [Fact]
    public void Read_Names_No_Server_So_The_Workbench_Url_Decides_Where_A_Call_Goes()
    {
        var services = GraphQLSdlImport.Read(Doc(HarborSdl));

        Assert.All(services, s => Assert.Null(s.OriginUrl));
    }

    [Fact]
    public void Read_Honours_A_Schema_Block_That_Renames_The_Roots()
    {
        var services = GraphQLSdlImport.Read(Doc("""
            schema { query: Lookups }
            type Lookups { ping: String }
            """));

        var query = Assert.Single(services);
        Assert.Equal("Query", query.Name);
        Assert.Equal(["ping"], query.Methods.Select(m => m.Name));
    }

    [Fact]
    public void Read_Of_Types_Without_A_Root_Describes_No_Service()
    {
        Assert.Empty(GraphQLSdlImport.Read(Doc("type Ship { id: ID! }")));
    }

    [Fact]
    public void Read_Refuses_What_Is_Not_Sdl_With_The_Parsers_Position()
    {
        var ex = Assert.Throws<FormatException>(() => GraphQLSdlImport.Read(Doc("type Query { ship(: Ship }")));

        Assert.StartsWith("Not GraphQL SDL:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_Refuses_Bytes_That_Are_Not_Text()
    {
        var ex = Assert.Throws<FormatException>(() => GraphQLSdlImport.Read(new byte[] { 0x0A, 0xFF, 0xFE, 0x80 }));

        Assert.Contains("not text", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_Accepts_A_Byte_Order_Mark()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("type Query { ping: String }")];

        Assert.Single(GraphQLSdlImport.Read(withBom));
    }

    [Theory]
    [InlineData("type Query { ping: String }")]
    [InlineData("# comment first\n\nschema { query: Q }\ntype Q { a: Int }")]
    [InlineData("\"\"\"Docs\"\"\"\ninterface Node { id: ID! }")]
    [InlineData("extend type Query { more: Int }")]
    [InlineData("scalar DateTime")]
    [InlineData("directive @auth on FIELD_DEFINITION")]
    public void LooksLikeSdl_Recognises_Type_System_Definitions(string text)
    {
        Assert.True(GraphQLSdlImport.LooksLikeSdl(Doc(text)));
    }

    [Theory]
    [InlineData("""{"openapi":"3.0.0","components":{"schemas":{"A":{"type":"object"}}}}""")]
    [InlineData("openapi: 3.0.0\ncomponents:\n  schemas:\n    A:\n      type: object\n")]
    [InlineData("asyncapi: 3.0.0\nchannels:\n  a:\n    type: object\n")]
    [InlineData("<definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\"/>")]
    [InlineData("just some notes about a type of ship")]
    [InlineData("")]
    public void LooksLikeSdl_Leaves_Other_Documents_Alone(string text)
    {
        Assert.False(GraphQLSdlImport.LooksLikeSdl(Doc(text)));
    }

    [Fact]
    public void The_Plugin_Declares_The_Sdl_Kind_With_Its_Extensions()
    {
        using var protocol = new BowireGraphQLProtocol();

        var kind = Assert.Single(protocol.UploadedSchemaKinds);
        Assert.Equal("graphql-sdl", kind.Kind);
        Assert.Equal([".graphql", ".graphqls", ".gql"], kind.Extensions);
        Assert.Equal(2, protocol.ReadUploadedSchema(kind.Kind, Doc(HarborSdl), "harbor.graphql").Count);
    }
}
