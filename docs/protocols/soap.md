---
summary: 'In-tree SOAP plugin. WSDL 1.1 discovery, SOAP 1.1 / 1.2 invocation, automatic Fault detection. No WCF / SoapCore dependency.'
---

# SOAP Protocol

Bowire's SOAP plugin walks any WSDL 1.1 document, lists every PortType operation as a Bowire method, and POSTs SOAP envelopes against the discovered endpoint. SOAP 1.1 is the default wire; SOAP 1.2 is opt-in via a metadata key.

## Setup

In-tree plugin — no separate install needed.

### Standalone

```bash
bowire --url http://example.com/calc.asmx
```

### Embedded

```csharp
app.MapBowire(options =>
{
    options.ServerUrls.Add("http://example.com/calc.asmx");
});
```

## Discovery

`DiscoverAsync` fetches the WSDL by appending `?wsdl` to the URL when it isn't already there (URLs that already carry `?wsdl` or end in `.wsdl` are taken verbatim). The parser walks every `<portType>` and matches it to its `<binding>` + `<service>/<port>` so each operation knows its SOAPAction header. Each PortType becomes a Bowire service; each operation a `Unary` method.

## Invocation

The first JSON message is treated as the operation body and inlined inside the namespaced operation element. Pass raw XML for full control, or plain text for simple cases:

```xml
<a>3</a><b>4</b>
```

| Metadata key | Purpose | Default |
|--------------|---------|---------|
| `soap_version` | `"1.2"` flips envelope namespace + Content-Type; anything else → 1.1 | `1.1` |
| `soap_action` | SOAPAction header value (1.1) / `action=...` parameter (1.2) | discovered from WSDL |
| `target_namespace` | Namespace bound to the operation element | discovered from WSDL |
| `endpoint_url` | Override the POST target (WSDL on host A, service on host B) | from `<soap:address>` |

## Fault handling

SOAP `<Fault>` bodies surface with `Status="Fault"` and the fault XML in the response field — distinct from transport-level errors (`Status="HTTP 500"` etc.).

## Streaming

SOAP has no streaming primitive — `InvokeStreamAsync` always returns empty and `OpenChannelAsync` returns null.

## Sample

A hand-rolled SOAP-1.1 Calculator service (no WCF dependency) lives at [`samples/Soap/CalculatorService`](https://github.com/Kuestenlogik/Bowire/tree/main/samples/Soap/CalculatorService) — `dotnet run`, point Bowire at `http://localhost:5180/Calculator.asmx`.
