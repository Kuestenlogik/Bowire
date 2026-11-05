// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;

namespace Kuestenlogik.Bowire.Protocol.Soap;

/// <summary>What a SOAP operation is addressed by: its SOAPAction and the namespace of its body element.</summary>
internal sealed record SoapOperationAddress(string SoapAction, string TargetNamespace);

/// <summary>
/// The SOAPAction and target namespace of every operation of a WSDL (#1000).
/// Discovery read the action into display text only and the namespace not at
/// all, while invoke takes both from the call's metadata — so a discovered
/// call went out with <c>SOAPAction: ""</c> and an unqualified body element,
/// which a service that dispatches on either rejects.
/// </summary>
internal static class SoapOperationAddresses
{
    private static readonly XNamespace Wsdl11 = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Soap11 = "http://schemas.xmlsoap.org/wsdl/soap/";
    private static readonly XNamespace Soap12 = "http://schemas.xmlsoap.org/wsdl/soap12/";

    /// <summary>The address of each operation, keyed <c>PortType/Operation</c>.</summary>
    public static IReadOnlyDictionary<string, SoapOperationAddress> Parse(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new Dictionary<string, SoapOperationAddress>(StringComparer.Ordinal);
        var defs = document.Root;
        if (defs is null || defs.Name != Wsdl11 + "definitions") return result;
        var wsdlNamespace = defs.Attribute("targetNamespace")?.Value ?? "";

        foreach (var portType in defs.Elements(Wsdl11 + "portType"))
        {
            var ptName = portType.Attribute("name")?.Value ?? "";
            if (ptName.Length == 0) continue;
            var binding = defs.Elements(Wsdl11 + "binding")
                .FirstOrDefault(b => WsdlParser.StripPrefix(b.Attribute("type")?.Value ?? "") == ptName);

            foreach (var op in portType.Elements(Wsdl11 + "operation"))
            {
                var opName = op.Attribute("name")?.Value ?? "";
                if (opName.Length == 0) continue;
                var bindingOp = binding?.Elements(Wsdl11 + "operation")
                    .FirstOrDefault(o => o.Attribute("name")?.Value == opName);

                var action = bindingOp?.Element(Soap11 + "operation")?.Attribute("soapAction")?.Value
                    ?? bindingOp?.Element(Soap12 + "operation")?.Attribute("soapAction")?.Value
                    ?? "";

                // rpc style names the namespace on soap:body; document style
                // qualifies the body element by the namespace of the part's
                // element; the WSDL's own targetNamespace is the last resort.
                var input = bindingOp?.Element(Wsdl11 + "input");
                var rpcNamespace = input?.Element(Soap11 + "body")?.Attribute("namespace")?.Value
                    ?? input?.Element(Soap12 + "body")?.Attribute("namespace")?.Value;
                var ns = !string.IsNullOrEmpty(rpcNamespace)
                    ? rpcNamespace
                    : InputElementNamespace(defs, op) ?? wsdlNamespace;

                result[ptName + "/" + opName] = new SoapOperationAddress(action, ns);
            }
        }
        return result;
    }

    /// <summary>
    /// The address of <paramref name="operation"/>: under its port type when
    /// <paramref name="service"/> names one, else the operation's only
    /// address across all port types. Null when there is none or several.
    /// </summary>
    public static SoapOperationAddress? Find(
        IReadOnlyDictionary<string, SoapOperationAddress> addresses, string? service, string operation)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (!string.IsNullOrEmpty(service) && addresses.TryGetValue(service + "/" + operation, out var exact))
            return exact;
        var matches = addresses
            .Where(kv => kv.Key.EndsWith("/" + operation, StringComparison.Ordinal))
            .Select(kv => kv.Value)
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>The namespace of the element the operation's input message carries, or null.</summary>
    private static string? InputElementNamespace(XElement defs, XElement portTypeOperation)
    {
        var messageName = WsdlParser.StripPrefix(
            portTypeOperation.Element(Wsdl11 + "input")?.Attribute("message")?.Value ?? "");
        if (messageName.Length == 0) return null;
        var part = defs.Elements(Wsdl11 + "message")
            .FirstOrDefault(m => m.Attribute("name")?.Value == messageName)?
            .Elements(Wsdl11 + "part")
            .FirstOrDefault(p => p.Attribute("element") is not null);
        var qname = part?.Attribute("element")?.Value;
        if (string.IsNullOrEmpty(qname)) return null;
        var colon = qname.IndexOf(':', StringComparison.Ordinal);
        var prefix = colon >= 0 ? qname[..colon] : "";
        var ns = prefix.Length == 0 ? part!.GetDefaultNamespace() : part!.GetNamespaceOfPrefix(prefix);
        return ns is null || ns == XNamespace.None ? null : ns.NamespaceName;
    }
}
