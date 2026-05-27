// Minimal SOAP 1.1 sample service for the Bowire SOAP plugin demo.
// Exposes Add, Subtract, Multiply and Divide on a hand-rolled SOAP
// endpoint at /Calculator.asmx, with a discovery-friendly WSDL at
// /Calculator.asmx?wsdl. No SoapCore / WCF dependency — the wire is
// plain XML over HTTP so the sample stays portable across .NET LTS
// boundaries.

using System.Globalization;
using System.Xml.Linq;

const string ServiceNamespace = "http://example.com/calc";
const string EndpointPath = "/Calculator.asmx";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5180");

var app = builder.Build();

// ---- WSDL ----------------------------------------------------------------
// Returned when the URL carries ?wsdl. Bowire's SOAP plugin will GET
// this on discovery, parse the PortType + binding, and surface the
// four operations under a "Calculator" service.
app.MapGet(EndpointPath, async (HttpContext ctx) =>
{
    if (!ctx.Request.Query.ContainsKey("wsdl"))
    {
        ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        await ctx.Response.WriteAsync("Use POST for SOAP requests or ?wsdl for the contract.");
        return;
    }

    ctx.Response.ContentType = "text/xml; charset=utf-8";
    await ctx.Response.WriteAsync(BuildWsdl(ctx.Request.Scheme + "://" + ctx.Request.Host + EndpointPath));
});

// ---- SOAP endpoint -------------------------------------------------------
// One handler for all four operations; we dispatch on the local-name
// of the first Body child since the SOAPAction header is optional in
// SOAP 1.2 and can be empty in 1.1.
app.MapPost(EndpointPath, async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    XDocument doc;
    try { doc = XDocument.Parse(body); }
    catch
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("Body is not valid XML.");
        return;
    }

    var op = doc.Descendants().FirstOrDefault(e => e.Parent?.Name.LocalName == "Body");
    if (op is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("Envelope contains no operation element.");
        return;
    }

    var a = ReadInt(op, "a");
    var b = ReadInt(op, "b");

    int result;
    try
    {
        result = op.Name.LocalName switch
        {
            "Add" => a + b,
            "Subtract" => a - b,
            "Multiply" => a * b,
            "Divide" => b == 0 ? throw new InvalidOperationException("divide by zero") : a / b,
            _ => throw new InvalidOperationException("unknown operation " + op.Name.LocalName),
        };
    }
    catch (Exception ex)
    {
        ctx.Response.ContentType = "text/xml; charset=utf-8";
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsync(BuildFault(ex.Message));
        return;
    }

    ctx.Response.ContentType = "text/xml; charset=utf-8";
    await ctx.Response.WriteAsync(BuildResponse(op.Name.LocalName, result));
});

await app.RunAsync();

static int ReadInt(XElement op, string partName)
{
    var el = op.Elements().FirstOrDefault(e =>
        e.Name.LocalName.Equals(partName, StringComparison.OrdinalIgnoreCase));
    return el is null
        ? 0
        : int.Parse(el.Value, CultureInfo.InvariantCulture);
}

static string BuildResponse(string opName, int value)
{
    var responseName = opName + "Response";
    return $"""
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <{responseName} xmlns="{ServiceNamespace}">
              <result>{value}</result>
            </{responseName}>
          </soap:Body>
        </soap:Envelope>
        """;
}

static string BuildFault(string message) => $"""
    <?xml version="1.0" encoding="utf-8"?>
    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
      <soap:Body>
        <soap:Fault>
          <faultcode>soap:Server</faultcode>
          <faultstring>{System.Net.WebUtility.HtmlEncode(message)}</faultstring>
        </soap:Fault>
      </soap:Body>
    </soap:Envelope>
    """;

static string BuildWsdl(string endpointUrl) => $"""
    <?xml version="1.0" encoding="utf-8"?>
    <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                 xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                 xmlns:xs="http://www.w3.org/2001/XMLSchema"
                 xmlns:tns="{ServiceNamespace}"
                 targetNamespace="{ServiceNamespace}">
      <message name="AddRequest">
        <part name="a" type="xs:int"/>
        <part name="b" type="xs:int"/>
      </message>
      <message name="AddResponse">
        <part name="result" type="xs:int"/>
      </message>
      <message name="SubtractRequest">
        <part name="a" type="xs:int"/>
        <part name="b" type="xs:int"/>
      </message>
      <message name="SubtractResponse">
        <part name="result" type="xs:int"/>
      </message>
      <message name="MultiplyRequest">
        <part name="a" type="xs:int"/>
        <part name="b" type="xs:int"/>
      </message>
      <message name="MultiplyResponse">
        <part name="result" type="xs:int"/>
      </message>
      <message name="DivideRequest">
        <part name="a" type="xs:int"/>
        <part name="b" type="xs:int"/>
      </message>
      <message name="DivideResponse">
        <part name="result" type="xs:int"/>
      </message>
      <portType name="Calculator">
        <operation name="Add">
          <documentation>Returns a + b.</documentation>
          <input message="tns:AddRequest"/>
          <output message="tns:AddResponse"/>
        </operation>
        <operation name="Subtract">
          <documentation>Returns a - b.</documentation>
          <input message="tns:SubtractRequest"/>
          <output message="tns:SubtractResponse"/>
        </operation>
        <operation name="Multiply">
          <documentation>Returns a * b.</documentation>
          <input message="tns:MultiplyRequest"/>
          <output message="tns:MultiplyResponse"/>
        </operation>
        <operation name="Divide">
          <documentation>Returns a / b. Faults on zero divisor.</documentation>
          <input message="tns:DivideRequest"/>
          <output message="tns:DivideResponse"/>
        </operation>
      </portType>
      <binding name="CalculatorSoap" type="tns:Calculator">
        <soap:binding style="document" transport="http://schemas.xmlsoap.org/soap/http"/>
        <operation name="Add">
          <soap:operation soapAction="{ServiceNamespace}/Add"/>
        </operation>
        <operation name="Subtract">
          <soap:operation soapAction="{ServiceNamespace}/Subtract"/>
        </operation>
        <operation name="Multiply">
          <soap:operation soapAction="{ServiceNamespace}/Multiply"/>
        </operation>
        <operation name="Divide">
          <soap:operation soapAction="{ServiceNamespace}/Divide"/>
        </operation>
      </binding>
      <service name="CalculatorService">
        <port name="CalculatorSoap" binding="tns:CalculatorSoap">
          <soap:address location="{endpointUrl}"/>
        </port>
      </service>
    </definitions>
    """;
