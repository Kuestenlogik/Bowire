// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Kuestenlogik.Bowire.Protocol.Soap.Tests;

/// <summary>
/// An uploaded WSDL read into the port types <c>?wsdl</c> would have
/// described (#994).
/// </summary>
public sealed class WsdlImportTests
{
    private const string CalculatorWsdl = """
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                     xmlns:tns="http://example.com/calc/wsdl"
                     xmlns:s="http://example.com/calc/schema"
                     targetNamespace="http://example.com/calc/wsdl">
          <message name="AddIn"><part name="parameters" element="s:Add"/></message>
          <portType name="Calculator">
            <operation name="Add"><input message="tns:AddIn"/></operation>
          </portType>
          <binding name="CalculatorSoap" type="tns:Calculator">
            <operation name="Add"><soap:operation soapAction="http://example.com/calc/Add"/></operation>
          </binding>
          <service name="CalculatorService">
            <port name="CalculatorSoap" binding="tns:CalculatorSoap">
              <soap:address location="ADDRESS"/>
            </port>
          </service>
        </definitions>
        """;

    private static ReadOnlyMemory<byte> Doc(string text) => Encoding.UTF8.GetBytes(text);

    private static string Wsdl(string address) => CalculatorWsdl.Replace("ADDRESS", address, StringComparison.Ordinal);

    [Fact]
    public void The_Plugin_Declares_Wsdl_Under_Its_Own_Extension()
    {
        using var protocol = new BowireSoapProtocol();

        var kind = Assert.Single(protocol.UploadedSchemaKinds);
        Assert.Equal("wsdl", kind.Kind);
        Assert.Equal([".wsdl"], kind.Extensions);
    }

    [Fact]
    public void Read_Lists_The_Port_Type_With_Its_Operations()
    {
        using var protocol = new BowireSoapProtocol();

        var calculator = Assert.Single(protocol.ReadUploadedSchema(
            "wsdl", Doc(Wsdl("http://calc.internal/Calculator.asmx")), "calculator.wsdl"));

        Assert.Equal("Calculator", calculator.Name);
        Assert.Equal("soap", calculator.Source);
        var add = Assert.Single(calculator.Methods);
        Assert.Equal("Add", add.Name);
        Assert.Equal("SOAPAction: http://example.com/calc/Add", add.Summary);
    }

    [Fact]
    public void Read_Addresses_The_Service_Where_Its_Own_Soap_Address_Says()
    {
        using var protocol = new BowireSoapProtocol();

        var calculator = Assert.Single(protocol.ReadUploadedSchema(
            "wsdl", Doc(Wsdl("http://calc.internal/Calculator.asmx")), "calculator.wsdl"));

        Assert.Equal("http://calc.internal/Calculator.asmx", calculator.OriginUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("REPLACE_WITH_ACTUAL_URL")]
    [InlineData("jms://queue/calc")]
    public void Read_Leaves_The_Origin_Open_When_The_Address_Is_Not_One_To_Call(string address)
    {
        // The workbench's URL then decides where the call goes.
        using var protocol = new BowireSoapProtocol();

        var calculator = Assert.Single(protocol.ReadUploadedSchema("wsdl", Doc(Wsdl(address)), "calculator.wsdl"));

        Assert.Null(calculator.OriginUrl);
    }

    [Theory]
    [InlineData("<description xmlns=\"http://www.w3.org/ns/wsdl\"/>", "<description>")]
    [InlineData("<edmx:Edmx xmlns:edmx=\"http://docs.oasis-open.org/odata/ns/edmx\" Version=\"4.0\"/>", "<Edmx>")]
    [InlineData("<definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\"><unclosed>", "not XML")]
    [InlineData("type Query { ping: String }", "not XML")]
    public void Read_Refuses_What_Is_Not_Wsdl_11_And_Says_What_It_Found(string text, string expected)
    {
        using var protocol = new BowireSoapProtocol();

        var ex = Assert.Throws<FormatException>(() => protocol.ReadUploadedSchema("wsdl", Doc(text), "x.wsdl"));

        Assert.StartsWith("Not a WSDL", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\"/>", true)]
    [InlineData("<?xml version=\"1.0\"?><wsdl:definitions xmlns:wsdl=\"http://schemas.xmlsoap.org/wsdl/\"/>", true)]
    // The name alone is not enough: XSD and others have a `definitions` too.
    [InlineData("<definitions xmlns=\"urn:something-else\"/>", false)]
    [InlineData("<edmx:Edmx xmlns:edmx=\"http://docs.oasis-open.org/odata/ns/edmx\"/>", false)]
    [InlineData("definitions", false)]
    public void LooksLike_Goes_By_The_Root_Element_And_Its_Namespace(string text, bool expected)
    {
        using var protocol = new BowireSoapProtocol();

        Assert.Equal(expected, protocol.LooksLikeUploadedSchema("wsdl", Doc(text)));
    }
}
