// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Kuestenlogik.Bowire.Protocol.JsonRpc.Tests;

/// <summary>
/// An uploaded OpenRPC document read into the service <c>rpc.discover</c>
/// would have produced (#994).
/// </summary>
public sealed class OpenRpcImportTests
{
    private const string HarborOpenRpc = """
        {
          "openrpc": "1.3.2",
          "info": { "title": "Harbor", "version": "1.0.0" },
          "methods": [
            {
              "name": "ship.get",
              "summary": "One ship by id",
              "params": [ { "name": "id", "required": true, "schema": { "type": "integer" } } ],
              "result": { "name": "ship", "schema": { "type": "object" } }
            },
            { "name": "ship.list", "params": [] }
          ]
        }
        """;

    private static ReadOnlyMemory<byte> Doc(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void The_Plugin_Declares_OpenRpc_As_Json()
    {
        using var protocol = new BowireJsonRpcProtocol();

        var kind = Assert.Single(protocol.UploadedSchemaKinds);
        Assert.Equal("openrpc", kind.Kind);
        Assert.Equal([".json"], kind.Extensions);
    }

    [Fact]
    public void Read_Lists_The_Methods_Under_The_Service_Discovery_Uses()
    {
        using var protocol = new BowireJsonRpcProtocol();

        var service = Assert.Single(protocol.ReadUploadedSchema("openrpc", Doc(HarborOpenRpc), "harbor.json"));

        Assert.Equal("Methods", service.Name);
        Assert.Equal("jsonrpc", service.Source);
        // No origin of its own: the URL the workbench is pointed at decides.
        Assert.Null(service.OriginUrl);
        Assert.Equal(["ship.get", "ship.list"], service.Methods.Select(m => m.Name));

        var id = Assert.Single(service.Methods[0].InputType.Fields);
        Assert.Equal("id", id.Name);
        Assert.Equal("integer", id.Type);
        Assert.True(id.Required);
        Assert.Contains("harbor.json", service.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_Of_A_Document_Without_Methods_Describes_No_Service()
    {
        using var protocol = new BowireJsonRpcProtocol();

        Assert.Empty(protocol.ReadUploadedSchema(
            "openrpc", Doc("""{"openrpc":"1.3.2","methods":[]}"""), "empty.json"));
    }

    [Theory]
    [InlineData("""{"openapi":"3.0.0","paths":{}}""", "openrpc")]
    [InlineData("""{"openrpc":"1.3.2"}""", "methods")]
    [InlineData("""["openrpc"]""", "openrpc")]
    [InlineData("openrpc: 1.3.2", "not JSON")]
    public void Read_Refuses_What_Is_Not_OpenRpc_And_Says_What_Is_Missing(string text, string expected)
    {
        using var protocol = new BowireJsonRpcProtocol();

        var ex = Assert.Throws<FormatException>(() => protocol.ReadUploadedSchema("openrpc", Doc(text), "x.json"));

        Assert.StartsWith("Not an OpenRPC document:", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"openrpc":"1.3.2","methods":[]}""", true)]
    [InlineData("""  { "info": {}, "openrpc": "1.3.2" }""", true)]
    [InlineData("""{"openapi":"3.0.0"}""", false)]
    [InlineData("""{"asyncapi":"3.0.0"}""", false)]
    // Nested is not top level: an OpenAPI document that mentions the word.
    [InlineData("""{"openapi":"3.0.0","info":{"openrpc":"no"}}""", false)]
    [InlineData("openapi: 3.0.0", false)]
    public void LooksLike_Goes_By_The_Top_Level_Version_Key(string text, bool expected)
    {
        using var protocol = new BowireJsonRpcProtocol();

        Assert.Equal(expected, protocol.LooksLikeUploadedSchema("openrpc", Doc(text)));
    }
}
