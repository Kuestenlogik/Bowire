// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Kuestenlogik.Bowire.Protocol.OData.Tests;

/// <summary>
/// An uploaded EDMX document read into the entity sets <c>$metadata</c>
/// would have described (#994).
/// </summary>
public sealed class EdmxImportTests
{
    private const string HarborEdmx = """
        <?xml version="1.0" encoding="utf-8"?>
        <edmx:Edmx xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx" Version="4.0">
          <edmx:DataServices>
            <Schema xmlns="http://docs.oasis-open.org/odata/ns/edm" Namespace="Harbor">
              <EntityType Name="Dock">
                <Key><PropertyRef Name="Code"/></Key>
                <Property Name="Code" Type="Edm.String" Nullable="false"/>
                <Property Name="LengthMeters" Type="Edm.Int32"/>
              </EntityType>
              <EntityContainer Name="HarborContainer">
                <EntitySet Name="Docks" EntityType="Harbor.Dock"/>
              </EntityContainer>
            </Schema>
          </edmx:DataServices>
        </edmx:Edmx>
        """;

    private static ReadOnlyMemory<byte> Doc(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void The_Plugin_Declares_Edmx_Under_Its_Own_Extensions()
    {
        using var protocol = new BowireODataProtocol();

        var kind = Assert.Single(protocol.UploadedSchemaKinds);
        Assert.Equal("edmx", kind.Kind);
        Assert.Equal([".edmx", ".csdl"], kind.Extensions);
    }

    [Fact]
    public void Read_Lists_Each_Entity_Set_With_Its_Crud_Methods()
    {
        using var protocol = new BowireODataProtocol();

        var docks = Assert.Single(protocol.ReadUploadedSchema("edmx", Doc(HarborEdmx), "harbor.edmx"));

        Assert.Equal("Docks", docks.Name);
        Assert.Equal("odata", docks.Source);
        // No origin of its own: an EDMX file names no service root.
        Assert.Null(docks.OriginUrl);
        Assert.Equal(
            ["GET", "GET_BY_KEY", "POST", "PATCH", "DELETE"],
            docks.Methods.Select(m => m.Name));
        Assert.Contains(docks.Methods.Single(m => m.Name == "POST").InputType.Fields, f => f.Name == "LengthMeters");
    }

    [Fact]
    public void Read_Of_A_Model_Without_A_Container_Describes_No_Service()
    {
        using var protocol = new BowireODataProtocol();

        Assert.Empty(protocol.ReadUploadedSchema("edmx", Doc("""
            <edmx:Edmx xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx" Version="4.0">
              <edmx:DataServices>
                <Schema xmlns="http://docs.oasis-open.org/odata/ns/edm" Namespace="Harbor"/>
              </edmx:DataServices>
            </edmx:Edmx>
            """), "types.edmx"));
    }

    [Theory]
    [InlineData("<definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\"/>")]
    [InlineData("<edmx:Edmx xmlns:edmx=\"http://docs.oasis-open.org/odata/ns/edmx\" Version=\"4.0\"><unclosed>")]
    [InlineData("{\"openapi\":\"3.0.0\"}")]
    public void Read_Refuses_What_Is_Not_Edmx(string text)
    {
        using var protocol = new BowireODataProtocol();

        var ex = Assert.Throws<FormatException>(() => protocol.ReadUploadedSchema("edmx", Doc(text), "x.edmx"));

        Assert.StartsWith("Not an OData EDMX document:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_Does_Not_Resolve_A_Dtd()
    {
        // A schema file is data from outside; an entity declaration in it
        // must not make the reader fetch or expand anything.
        using var protocol = new BowireODataProtocol();

        Assert.Throws<FormatException>(() => protocol.ReadUploadedSchema("edmx", Doc("""
            <!DOCTYPE x [ <!ENTITY e SYSTEM "file:///etc/hostname"> ]>
            <edmx:Edmx xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx" Version="4.0">&e;</edmx:Edmx>
            """), "x.edmx"));
    }

    [Theory]
    [InlineData("<edmx:Edmx xmlns:edmx=\"http://docs.oasis-open.org/odata/ns/edmx\" Version=\"4.0\"/>", true)]
    [InlineData("<?xml version=\"1.0\"?>\n<!-- saved from $metadata -->\n<Edmx xmlns=\"http://docs.oasis-open.org/odata/ns/edmx\"/>", true)]
    [InlineData("<definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\"/>", false)]
    [InlineData("{\"Edmx\":true}", false)]
    [InlineData("Edmx", false)]
    public void LooksLike_Goes_By_The_Root_Element(string text, bool expected)
    {
        using var protocol = new BowireODataProtocol();

        Assert.Equal(expected, protocol.LooksLikeUploadedSchema("edmx", Doc(text)));
    }
}
