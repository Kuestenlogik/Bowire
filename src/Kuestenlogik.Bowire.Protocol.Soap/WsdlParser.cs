// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.Soap;

/// <summary>
/// Parses a WSDL 1.1 document into the Bowire service/method tree. The
/// parser is intentionally narrow: it only walks the bits Bowire needs
/// to invoke (PortType operations + their SOAP binding + the endpoint
/// URL) and ignores XSD-level type detail beyond named parts. Bowire's
/// form UI renders one text input per part — full XSD-to-form mapping
/// is left to the freeform JSON-message field.
/// </summary>
internal static class WsdlParser
{
    private static readonly XNamespace Wsdl11 = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Soap11 = "http://schemas.xmlsoap.org/wsdl/soap/";
    private static readonly XNamespace Soap12 = "http://schemas.xmlsoap.org/wsdl/soap12/";

    /// <summary>
    /// Walk a parsed WSDL document and pull every operation into a
    /// Bowire service tree. Returns an empty list when the document
    /// isn't a recognisable WSDL (no &lt;definitions&gt; root).
    /// </summary>
    /// <param name="document">The XML document to walk.</param>
    /// <param name="originUrl">URL the WSDL was fetched from — set on
    /// each service for round-trip routing.</param>
    /// <returns>One <see cref="BowireServiceInfo"/> per WSDL PortType
    /// that has at least one SOAP-bound operation.</returns>
    public static List<BowireServiceInfo> Parse(XDocument document, string originUrl)
    {
        ArgumentNullException.ThrowIfNull(document);
        var defs = document.Root;
        if (defs is null || defs.Name != Wsdl11 + "definitions") return [];

        var bindings = ParseBindings(defs);

        var services = new List<BowireServiceInfo>();
        foreach (var portType in defs.Elements(Wsdl11 + "portType"))
        {
            var ptName = portType.Attribute("name")?.Value ?? "";
            if (string.IsNullOrEmpty(ptName)) continue;

            // Find the first binding that targets this portType — most
            // single-binding WSDLs have exactly one, so picking the
            // first is the pragmatic match.
            var binding = bindings.FirstOrDefault(b =>
                b.PortTypeLocalName.Equals(ptName, StringComparison.Ordinal));

            var methods = new List<BowireMethodInfo>();
            foreach (var op in portType.Elements(Wsdl11 + "operation"))
            {
                var opName = op.Attribute("name")?.Value ?? "";
                if (string.IsNullOrEmpty(opName)) continue;

                var input = BuildPartFields(op.Element(Wsdl11 + "input"), opName + "Request");
                var output = new BowireMessageInfo(opName + "Response", opName + "Response", []);
                var doc = op.Element(Wsdl11 + "documentation")?.Value?.Trim();
                var soapAction = binding?.Operations
                    .FirstOrDefault(o => o.Name == opName)?.SoapAction;

                methods.Add(new BowireMethodInfo(
                    Name: opName,
                    FullName: ptName + "/" + opName,
                    ClientStreaming: false,
                    ServerStreaming: false,
                    InputType: input,
                    OutputType: output,
                    MethodType: "Unary")
                {
                    Description = string.IsNullOrEmpty(doc) ? null : doc,
                    Summary = string.IsNullOrEmpty(soapAction) ? null : "SOAPAction: " + soapAction,
                });
            }

            if (methods.Count == 0) continue;

            services.Add(new BowireServiceInfo(ptName, "soap", methods)
            {
                Source = "soap",
                OriginUrl = originUrl,
                Description = binding?.EndpointUrl is { Length: > 0 } ep
                    ? "SOAP endpoint: " + ep
                    : null,
            });
        }

        return services;
    }

    /// <summary>
    /// Pull (binding, endpoint URL, SOAPAction-per-op) tuples out of the
    /// WSDL. Both SOAP 1.1 and 1.2 element names are walked since most
    /// real-world WSDLs declare bindings for either version (or both).
    /// </summary>
    private static List<BindingInfo> ParseBindings(XElement defs)
    {
        // Map binding name → endpoint URL via <service><port binding="...">.
        var endpoints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var svc in defs.Elements(Wsdl11 + "service"))
        {
            foreach (var port in svc.Elements(Wsdl11 + "port"))
            {
                var bindingQName = port.Attribute("binding")?.Value;
                if (string.IsNullOrEmpty(bindingQName)) continue;
                var localBinding = StripPrefix(bindingQName);

                var addr11 = port.Element(Soap11 + "address")?.Attribute("location")?.Value;
                var addr12 = port.Element(Soap12 + "address")?.Attribute("location")?.Value;
                var addr = addr11 ?? addr12;
                if (!string.IsNullOrEmpty(addr))
                    endpoints[localBinding] = addr!;
            }
        }

        var bindings = new List<BindingInfo>();
        foreach (var binding in defs.Elements(Wsdl11 + "binding"))
        {
            var name = binding.Attribute("name")?.Value ?? "";
            if (string.IsNullOrEmpty(name)) continue;

            var typeAttr = binding.Attribute("type")?.Value ?? "";
            var portTypeLocal = StripPrefix(typeAttr);

            var ops = new List<BindingOperation>();
            foreach (var op in binding.Elements(Wsdl11 + "operation"))
            {
                var opName = op.Attribute("name")?.Value ?? "";
                var sa11 = op.Element(Soap11 + "operation")?.Attribute("soapAction")?.Value;
                var sa12 = op.Element(Soap12 + "operation")?.Attribute("soapAction")?.Value;
                ops.Add(new BindingOperation(opName, sa11 ?? sa12 ?? ""));
            }

            bindings.Add(new BindingInfo(
                Name: name,
                PortTypeLocalName: portTypeLocal,
                EndpointUrl: endpoints.GetValueOrDefault(name),
                Operations: ops));
        }
        return bindings;
    }

    /// <summary>
    /// WSDL part-names become Bowire field-names. We don't follow the
    /// part's <c>element=</c> ref into the XSD type system; users get
    /// one string field per part and Bowire's freeform message field
    /// handles full-XML body payloads.
    /// </summary>
    private static BowireMessageInfo BuildPartFields(XElement? messageRef, string typeName)
    {
        var fields = new List<BowireFieldInfo>();
        // The portType/operation references a top-level message by qname.
        // For Bowire's form purposes the part names are enough.
        if (messageRef is null) return new BowireMessageInfo(typeName, typeName, fields);

        var msgQName = messageRef.Attribute("message")?.Value ?? "";
        var messageLocal = StripPrefix(msgQName);
        if (string.IsNullOrEmpty(messageLocal))
            return new BowireMessageInfo(typeName, typeName, fields);

        var defs = messageRef.AncestorsAndSelf(Wsdl11 + "definitions").FirstOrDefault();
        if (defs is null) return new BowireMessageInfo(typeName, typeName, fields);

        var msg = defs.Elements(Wsdl11 + "message")
            .FirstOrDefault(m => m.Attribute("name")?.Value == messageLocal);
        if (msg is null) return new BowireMessageInfo(typeName, typeName, fields);

        var i = 1;
        foreach (var part in msg.Elements(Wsdl11 + "part"))
        {
            var partName = part.Attribute("name")?.Value ?? $"part{i}";
            var elemRef = part.Attribute("element")?.Value;
            var typeRef = part.Attribute("type")?.Value;
            var t = StripPrefix(elemRef ?? typeRef ?? "");

            fields.Add(new BowireFieldInfo(
                Name: partName,
                Number: i++,
                Type: string.IsNullOrEmpty(t) ? "string" : t,
                Label: "optional",
                IsMap: false,
                IsRepeated: false,
                MessageType: null,
                EnumValues: null)
            {
                Source = "body",
            });
        }
        return new BowireMessageInfo(typeName, typeName, fields);
    }

    /// <summary>
    /// WSDL qnames look like <c>tns:Foo</c> — we only need the local
    /// part for matching. The prefix→namespace mapping doesn't matter
    /// because Bowire works on bare names throughout.
    /// </summary>
    internal static string StripPrefix(string qname)
    {
        if (string.IsNullOrEmpty(qname)) return "";
        var colon = qname.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? qname[(colon + 1)..] : qname;
    }

    internal sealed record BindingOperation(string Name, string SoapAction);

    internal sealed record BindingInfo(
        string Name,
        string PortTypeLocalName,
        string? EndpointUrl,
        List<BindingOperation> Operations);
}
