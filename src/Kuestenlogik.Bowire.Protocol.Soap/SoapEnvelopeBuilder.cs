// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Xml.Linq;

namespace Kuestenlogik.Bowire.Protocol.Soap;

/// <summary>
/// Wraps the JSON form payload (or raw XML body) into a SOAP envelope
/// for outbound POST, and unwraps the response envelope back to a
/// pretty-printed XML body string for display.
/// </summary>
/// <remarks>
/// We default to SOAP 1.1 because it still dominates in the wild and is
/// what most ?wsdl-published endpoints expect when no Content-Type hint
/// is offered. SOAP 1.2 is opt-in via the <c>soap_version</c> metadata
/// field on the invoke call (<c>"1.2"</c>); anything else falls back
/// to 1.1.
/// </remarks>
internal static class SoapEnvelopeBuilder
{
    public const string Soap11Envelope = "http://schemas.xmlsoap.org/soap/envelope/";
    public const string Soap12Envelope = "http://www.w3.org/2003/05/soap-envelope";

    /// <summary>
    /// Build a SOAP envelope wrapping the operation name + arguments.
    /// </summary>
    /// <param name="operationName">The WSDL operation name; becomes the
    /// child of &lt;Body&gt;.</param>
    /// <param name="targetNamespace">Namespace bound to the operation
    /// element. Empty string means "no namespace" (rare; mostly for
    /// hand-rolled mocks).</param>
    /// <param name="bodyXml">
    /// Payload shape — if the string parses as XML it's inlined inside
    /// the operation element verbatim (caller supplies their own
    /// namespace-correct arguments). Otherwise it's treated as plain
    /// text content of the operation element.
    /// </param>
    /// <param name="soapVersion">"1.2" picks the WS-I SOAP 1.2 envelope
    /// namespace; anything else (including null) → SOAP 1.1.</param>
    public static string BuildRequestEnvelope(
        string operationName, string targetNamespace, string bodyXml, string? soapVersion)
    {
        var envNs = soapVersion == "1.2" ? Soap12Envelope : Soap11Envelope;

        XNamespace soap = envNs;
        XElement opElement;
        if (string.IsNullOrEmpty(targetNamespace))
        {
            opElement = new XElement(operationName);
        }
        else
        {
            XNamespace tns = targetNamespace;
            opElement = new XElement(tns + operationName);
        }

        if (!string.IsNullOrWhiteSpace(bodyXml))
        {
            try
            {
                // Accept a full XML fragment as the operation's child
                // payload — keeps callers in control of namespace + part
                // ordering. Wrap in a synthetic root because XDocument
                // refuses bare fragments.
                var doc = XDocument.Parse("<root>" + bodyXml + "</root>");
                foreach (var n in doc.Root!.Nodes())
                    opElement.Add(n);
            }
            catch (System.Xml.XmlException)
            {
                opElement.Value = bodyXml;
            }
        }

        var envelope = new XElement(soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", envNs),
            new XElement(soap + "Body", opElement));

        return new XDeclaration("1.0", "utf-8", null) + envelope.ToString();
    }

    /// <summary>
    /// Extract the body payload from a SOAP response. For successful
    /// responses we strip the Envelope+Body wrappers and pretty-print
    /// the operation reply element so the workbench shows the actual
    /// payload, not the envelope plumbing. SOAP Faults are returned
    /// verbatim under a dedicated label so callers can flag them.
    /// </summary>
    public static SoapResponse ParseResponseEnvelope(string responseXml)
    {
        ArgumentNullException.ThrowIfNull(responseXml);
        if (string.IsNullOrWhiteSpace(responseXml))
            return new SoapResponse(false, "", "Empty response body");

        XDocument doc;
        try { doc = XDocument.Parse(responseXml); }
        catch (System.Xml.XmlException) { return new SoapResponse(false, responseXml, "Not XML"); }

        var envelope = doc.Root;
        if (envelope is null || envelope.Name.LocalName != "Envelope")
            return new SoapResponse(false, responseXml, "Not a SOAP envelope");

        var body = envelope.Elements().FirstOrDefault(e => e.Name.LocalName == "Body");
        if (body is null)
            return new SoapResponse(false, responseXml, "No <Body> in envelope");

        var fault = body.Elements().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault is not null)
            return new SoapResponse(true, fault.ToString(), null);

        // The first non-fault child of Body is the operation response
        // wrapper (operationNameResponse by convention). Pretty-print it
        // verbatim — Bowire displays the result as text.
        var payload = body.Elements().FirstOrDefault();
        return payload is null
            ? new SoapResponse(false, body.ToString(), null)
            : new SoapResponse(false, payload.ToString(), null);
    }

    /// <summary>
    /// Compute the Content-Type header for a SOAP POST. SOAP 1.1 wants
    /// <c>text/xml</c> + a separate <c>SOAPAction</c> header; SOAP 1.2
    /// folds the action into the Content-Type as a parameter.
    /// </summary>
    public static string ContentTypeFor(string? soapVersion, string? soapAction)
    {
        if (soapVersion == "1.2")
        {
            return string.IsNullOrEmpty(soapAction)
                ? "application/soap+xml; charset=utf-8"
                : $"application/soap+xml; charset=utf-8; action=\"{soapAction}\"";
        }
        return "text/xml; charset=utf-8";
    }

    public static Encoding Utf8NoBom { get; } = new UTF8Encoding(false);

    internal sealed record SoapResponse(bool IsFault, string Body, string? Status);
}
