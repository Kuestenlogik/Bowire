// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using Kuestenlogik.Bowire.Protocol.Soap;
using Xunit;

namespace Kuestenlogik.Bowire.Protocol.Soap.Tests;

/// <summary>
/// Edge-case branches in <see cref="WsdlParser"/> and
/// <see cref="SoapEnvelopeBuilder"/> that the happy-path tests in
/// <see cref="SoapPluginTests"/> don't reach. Targets the
/// "defensive null / missing element" branches the parser hits when
/// WSDLs are malformed or unusual.
/// </summary>
public class SoapEdgeCaseTests
{
    [Fact]
    public void Wsdl_With_No_Bindings_Or_Service_Still_Surfaces_Operations()
    {
        // PortType present, no binding, no service — Bowire should
        // still list operations (SOAPAction summary just stays null).
        const string wsdl = """
        <?xml version="1.0"?>
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:tns="http://x/" targetNamespace="http://x/">
          <message name="PingRequest"/>
          <message name="PingResponse"/>
          <portType name="Pinger">
            <operation name="Ping">
              <input message="tns:PingRequest"/>
              <output message="tns:PingResponse"/>
            </operation>
          </portType>
        </definitions>
        """;
        var svcs = WsdlParser.Parse(XDocument.Parse(wsdl), "http://x/");
        var svc = Assert.Single(svcs);
        Assert.Null(svc.Description);
        Assert.Null(Assert.Single(svc.Methods).Summary);
    }

    [Fact]
    public void Wsdl_PortType_With_No_Operations_Is_Skipped()
    {
        const string wsdl = """
        <?xml version="1.0"?>
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:tns="http://x/" targetNamespace="http://x/">
          <portType name="Empty"/>
        </definitions>
        """;
        Assert.Empty(WsdlParser.Parse(XDocument.Parse(wsdl), "http://x/"));
    }

    [Fact]
    public void Wsdl_Operation_With_Missing_Name_Is_Skipped()
    {
        const string wsdl = """
        <?xml version="1.0"?>
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:tns="http://x/" targetNamespace="http://x/">
          <portType name="X">
            <operation>
              <input message="tns:Nope"/>
            </operation>
            <operation name="Real">
              <input message="tns:RealRequest"/>
            </operation>
          </portType>
          <message name="RealRequest"/>
        </definitions>
        """;
        var svc = Assert.Single(WsdlParser.Parse(XDocument.Parse(wsdl), "http://x/"));
        Assert.Equal("Real", Assert.Single(svc.Methods).Name);
    }

    [Fact]
    public void Wsdl_Input_Without_Message_Yields_Empty_Field_List()
    {
        const string wsdl = """
        <?xml version="1.0"?>
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:tns="http://x/" targetNamespace="http://x/">
          <portType name="X">
            <operation name="NoMsg">
              <input/>
            </operation>
          </portType>
        </definitions>
        """;
        var svc = Assert.Single(WsdlParser.Parse(XDocument.Parse(wsdl), "http://x/"));
        Assert.Empty(Assert.Single(svc.Methods).InputType.Fields);
    }

    [Fact]
    public void Wsdl_Message_Reference_To_Missing_Definition_Yields_Empty_Fields()
    {
        const string wsdl = """
        <?xml version="1.0"?>
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:tns="http://x/" targetNamespace="http://x/">
          <portType name="X">
            <operation name="Op">
              <input message="tns:NotDefinedAnywhere"/>
            </operation>
          </portType>
        </definitions>
        """;
        var svc = Assert.Single(WsdlParser.Parse(XDocument.Parse(wsdl), "http://x/"));
        Assert.Empty(Assert.Single(svc.Methods).InputType.Fields);
    }

    [Fact]
    public void Wsdl_Part_Without_Element_Or_Type_Defaults_To_String()
    {
        const string wsdl = """
        <?xml version="1.0"?>
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:tns="http://x/" targetNamespace="http://x/">
          <message name="Req">
            <part name="bare"/>
          </message>
          <portType name="X">
            <operation name="Op">
              <input message="tns:Req"/>
            </operation>
          </portType>
        </definitions>
        """;
        var svc = Assert.Single(WsdlParser.Parse(XDocument.Parse(wsdl), "http://x/"));
        var field = Assert.Single(Assert.Single(svc.Methods).InputType.Fields);
        Assert.Equal("string", field.Type);
    }

    [Fact]
    public void Wsdl_Part_Without_Name_Gets_Synthetic_Part_Name()
    {
        const string wsdl = """
        <?xml version="1.0"?>
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:tns="http://x/" targetNamespace="http://x/">
          <message name="Req">
            <part type="xs:int"/>
          </message>
          <portType name="X">
            <operation name="Op">
              <input message="tns:Req"/>
            </operation>
          </portType>
        </definitions>
        """;
        var svc = Assert.Single(WsdlParser.Parse(XDocument.Parse(wsdl), "http://x/"));
        var field = Assert.Single(Assert.Single(svc.Methods).InputType.Fields);
        Assert.Equal("part1", field.Name);
    }

    [Fact]
    public void Wsdl_Binding_With_Soap12_Operation_Picks_Up_SoapAction()
    {
        const string wsdl = """
        <?xml version="1.0"?>
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:soap12="http://schemas.xmlsoap.org/wsdl/soap12/"
                     xmlns:tns="http://x/" targetNamespace="http://x/">
          <message name="Req"/>
          <message name="Res"/>
          <portType name="X">
            <operation name="Op">
              <input message="tns:Req"/>
              <output message="tns:Res"/>
            </operation>
          </portType>
          <binding name="XB" type="tns:X">
            <operation name="Op">
              <soap12:operation soapAction="http://x/Op12"/>
            </operation>
          </binding>
          <service name="S">
            <port name="P" binding="tns:XB">
              <soap12:address location="http://x/svc"/>
            </port>
          </service>
        </definitions>
        """;
        var svc = Assert.Single(WsdlParser.Parse(XDocument.Parse(wsdl), "http://x/"));
        Assert.Contains("http://x/Op12", Assert.Single(svc.Methods).Summary ?? "");
        Assert.Contains("http://x/svc", svc.Description ?? "");
    }

    [Fact]
    public void Parse_Returns_Empty_For_Document_With_Wrong_Root_Namespace()
    {
        // Right local name "definitions" but wrong namespace -> bail.
        var doc = XDocument.Parse("<definitions xmlns=\"urn:not-wsdl\"/>");
        Assert.Empty(WsdlParser.Parse(doc, "http://x/"));
    }

    [Fact]
    public void Envelope_Without_Target_Namespace_Builds_Bare_Operation_Element()
    {
        var env = SoapEnvelopeBuilder.BuildRequestEnvelope(
            "Op", targetNamespace: "", bodyXml: "<a>1</a>", soapVersion: "1.1");
        // No xmlns attribute on <Op>.
        Assert.Contains("<Op>", env);
    }

    [Fact]
    public void Envelope_With_Empty_Body_Skips_Inline_Body()
    {
        var env = SoapEnvelopeBuilder.BuildRequestEnvelope(
            "Op", targetNamespace: "", bodyXml: "", soapVersion: "1.1");
        Assert.Contains("<Op", env);
        Assert.DoesNotContain("<a>", env);
    }

    [Fact]
    public void Parses_Body_With_Multiple_Children_Returns_First_NonFault()
    {
        const string body = """
        <?xml version="1.0"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <First/>
            <Second/>
          </soap:Body>
        </soap:Envelope>
        """;
        var r = SoapEnvelopeBuilder.ParseResponseEnvelope(body);
        Assert.False(r.IsFault);
        Assert.Contains("<First", r.Body);
    }

    [Fact]
    public void Parse_Response_Returns_Body_When_No_Children()
    {
        const string body = """
        <?xml version="1.0"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body></soap:Body>
        </soap:Envelope>
        """;
        var r = SoapEnvelopeBuilder.ParseResponseEnvelope(body);
        Assert.False(r.IsFault);
        // Returns the body element verbatim when there's nothing inside.
        Assert.Contains("Body", r.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Response_Handles_Empty_Input(string body)
    {
        var r = SoapEnvelopeBuilder.ParseResponseEnvelope(body);
        Assert.False(r.IsFault);
        Assert.Equal("Empty response body", r.Status);
    }

    [Fact]
    public void Parse_Response_Handles_Non_Xml_Input()
    {
        var r = SoapEnvelopeBuilder.ParseResponseEnvelope("not xml");
        Assert.Equal("Not XML", r.Status);
    }

    [Fact]
    public void Parse_Response_Handles_Xml_That_Is_Not_An_Envelope()
    {
        var r = SoapEnvelopeBuilder.ParseResponseEnvelope("<root/>");
        Assert.Equal("Not a SOAP envelope", r.Status);
    }

    [Fact]
    public void Parse_Response_Handles_Envelope_Without_Body()
    {
        const string xml = "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Header/></soap:Envelope>";
        var r = SoapEnvelopeBuilder.ParseResponseEnvelope(xml);
        Assert.Equal("No <Body> in envelope", r.Status);
    }
}
