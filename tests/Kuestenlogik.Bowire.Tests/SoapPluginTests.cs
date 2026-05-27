// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using Kuestenlogik.Bowire.Protocol.Soap;
using Xunit;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit-level coverage for the SOAP plugin's pure helpers: URL plumbing,
/// WSDL parsing into the Bowire service tree, and SOAP envelope
/// build/parse round-trips. The HTTP I/O side of <c>BowireSoapProtocol</c>
/// is exercised by the integration suite once a sample SOAP service is
/// in place.
/// </summary>
public class SoapPluginTests
{
    [Fact]
    public void Plugin_Identity_Is_Stable()
    {
        var p = new BowireSoapProtocol();
        Assert.Equal("SOAP", p.Name);
        Assert.Equal("soap", p.Id);
        Assert.False(string.IsNullOrWhiteSpace(p.IconSvg));
    }

    [Theory]
    [InlineData("http://x/svc", "http://x/svc?wsdl")]
    [InlineData("http://x/svc?wsdl", "http://x/svc?wsdl")]
    [InlineData("https://x/svc.wsdl", "https://x/svc.wsdl")]
    [InlineData("http://x/svc?foo=bar", "http://x/svc?foo=bar&wsdl")]
    public void Resolves_Wsdl_Uri(string input, string expected)
    {
        Assert.True(BowireSoapProtocol.TryResolveWsdlUri(input, out var uri));
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://x")]
    [InlineData("not-a-url")]
    public void Rejects_Non_Http_Urls(string input)
    {
        Assert.False(BowireSoapProtocol.TryResolveWsdlUri(input, out _));
    }

    [Fact]
    public void Endpoint_Uri_Strips_Wsdl_Query()
    {
        Assert.True(BowireSoapProtocol.TryResolveEndpointUri("http://x/svc?wsdl", out var u));
        Assert.Equal("http://x/svc", u.ToString());
    }

    [Theory]
    [InlineData("Calculator/Add", "Add")]
    [InlineData("Add", "Add")]
    [InlineData("", "")]
    public void Extracts_Operation_Name_From_FullName(string method, string expected)
    {
        Assert.Equal(expected, BowireSoapProtocol.ExtractOperationName(method));
    }

    [Fact]
    public void StripPrefix_Returns_Local_Part()
    {
        Assert.Equal("Foo", WsdlParser.StripPrefix("tns:Foo"));
        Assert.Equal("Foo", WsdlParser.StripPrefix("Foo"));
        Assert.Equal("", WsdlParser.StripPrefix(""));
    }

    [Fact]
    public void Parses_Minimal_Wsdl_Into_Service_Tree()
    {
        // Trimmed real-world WSDL 1.1: one PortType (Calculator),
        // one operation (Add), one SOAP 1.1 binding pointing at
        // /calc.asmx.
        const string wsdl = """
        <?xml version="1.0" encoding="utf-8"?>
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                     xmlns:tns="http://example.com/calc"
                     targetNamespace="http://example.com/calc">
          <message name="AddRequest">
            <part name="a" type="xs:int"/>
            <part name="b" type="xs:int"/>
          </message>
          <message name="AddResponse">
            <part name="result" type="xs:int"/>
          </message>
          <portType name="Calculator">
            <operation name="Add">
              <documentation>Adds two integers</documentation>
              <input message="tns:AddRequest"/>
              <output message="tns:AddResponse"/>
            </operation>
          </portType>
          <binding name="CalculatorSoap" type="tns:Calculator">
            <soap:binding style="document" transport="http://schemas.xmlsoap.org/soap/http"/>
            <operation name="Add">
              <soap:operation soapAction="http://example.com/calc/Add"/>
            </operation>
          </binding>
          <service name="CalculatorService">
            <port name="CalculatorSoap" binding="tns:CalculatorSoap">
              <soap:address location="http://example.com/calc.asmx"/>
            </port>
          </service>
        </definitions>
        """;

        var doc = XDocument.Parse(wsdl);
        var services = WsdlParser.Parse(doc, "http://example.com/calc.asmx?wsdl");

        var svc = Assert.Single(services);
        Assert.Equal("Calculator", svc.Name);
        Assert.Equal("soap", svc.Source);
        Assert.Contains("calc.asmx", svc.Description);

        var add = Assert.Single(svc.Methods);
        Assert.Equal("Add", add.Name);
        Assert.Equal("Calculator/Add", add.FullName);
        Assert.Equal("Unary", add.MethodType);
        Assert.Equal("Adds two integers", add.Description);
        Assert.Contains("SOAPAction", add.Summary ?? "");
        // Two part-names -> two fields.
        Assert.Equal(2, add.InputType.Fields.Count);
        Assert.Equal("a", add.InputType.Fields[0].Name);
        Assert.Equal("b", add.InputType.Fields[1].Name);
    }

    [Fact]
    public void Parse_Returns_Empty_For_Non_Wsdl_Xml()
    {
        var doc = XDocument.Parse("<root><foo/></root>");
        Assert.Empty(WsdlParser.Parse(doc, "http://x"));
    }

    [Fact]
    public void Builds_Soap_1_1_Envelope_With_Operation_Element()
    {
        var env = SoapEnvelopeBuilder.BuildRequestEnvelope(
            "Add", "http://example.com/calc", "<a>1</a><b>2</b>", "1.1");

        // XElement.Add re-emits children with their original (empty)
        // namespace, so the literal "<a>1</a>" becomes "<a xmlns="">1</a>"
        // when nested inside the namespaced operation element. We check
        // for the values, not the literal source strings.
        Assert.Contains("http://schemas.xmlsoap.org/soap/envelope/", env);
        Assert.Contains(">1<", env);
        Assert.Contains(">2<", env);
        Assert.Contains("Add", env);
        Assert.Contains("Body", env);
    }

    [Fact]
    public void Builds_Soap_1_2_Envelope_With_Different_Namespace()
    {
        var env = SoapEnvelopeBuilder.BuildRequestEnvelope(
            "Add", "http://example.com/calc", "<a>1</a>", "1.2");

        Assert.Contains("http://www.w3.org/2003/05/soap-envelope", env);
    }

    [Fact]
    public void Body_Falls_Back_To_Text_When_Not_Xml()
    {
        var env = SoapEnvelopeBuilder.BuildRequestEnvelope(
            "Echo", "http://example.com/x", "plain text payload", "1.1");
        Assert.Contains("plain text payload", env);
    }

    [Fact]
    public void Parses_Response_Envelope_And_Extracts_Body_Payload()
    {
        const string reply = """
        <?xml version="1.0"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <AddResponse xmlns="http://example.com/calc">
              <result>3</result>
            </AddResponse>
          </soap:Body>
        </soap:Envelope>
        """;
        var r = SoapEnvelopeBuilder.ParseResponseEnvelope(reply);
        Assert.False(r.IsFault);
        Assert.Contains("<result>3</result>", r.Body);
    }

    [Fact]
    public void Parses_Soap_Fault_And_Sets_IsFault()
    {
        const string fault = """
        <?xml version="1.0"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <soap:Fault>
              <faultcode>soap:Server</faultcode>
              <faultstring>boom</faultstring>
            </soap:Fault>
          </soap:Body>
        </soap:Envelope>
        """;
        var r = SoapEnvelopeBuilder.ParseResponseEnvelope(fault);
        Assert.True(r.IsFault);
        Assert.Contains("faultstring", r.Body);
    }

    [Theory]
    [InlineData(null, "", "text/xml; charset=utf-8")]
    [InlineData("1.1", "http://x", "text/xml; charset=utf-8")]
    [InlineData("1.2", "", "application/soap+xml; charset=utf-8")]
    [InlineData("1.2", "http://x/Op", "application/soap+xml; charset=utf-8; action=\"http://x/Op\"")]
    public void Content_Type_Picks_Up_Soap_Version_And_Action(string? version, string action, string expected)
    {
        Assert.Equal(expected, SoapEnvelopeBuilder.ContentTypeFor(version, action));
    }

    [Fact]
    public void Plugin_Settings_Expose_Default_Soap_Version()
    {
        var p = new BowireSoapProtocol();
        var s = Assert.Single(p.Settings);
        Assert.Equal("defaultSoapVersion", s.Key);
    }
}
