// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Kuestenlogik.Bowire.AsyncApi;
using Kuestenlogik.Bowire.Protocol.JsonRpc;
using Kuestenlogik.Bowire.Protocol.OData;
using Kuestenlogik.Bowire.Protocol.Soap;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Schema documents of the kinds the plugins import, stored the way the
/// upload endpoint stores them, then listed and called (#994).
/// </summary>
/// <remarks>
/// Each case is a server that does not hand out its own description: no
/// <c>?wsdl</c>, no <c>$metadata</c>, no <c>rpc.discover</c>. The uploaded
/// document has to carry the listing and whatever the call needs from it.
/// </remarks>
public sealed class UploadedSchemaKindsIntegrationTests : IDisposable
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
        </definitions>
        """;

    private const string PeopleEdmx = """
        <edmx:Edmx xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx" Version="4.0">
          <edmx:DataServices>
            <Schema xmlns="http://docs.oasis-open.org/odata/ns/edm" Namespace="Demo">
              <EntityType Name="Person">
                <Key><PropertyRef Name="UserName"/></Key>
                <Property Name="UserName" Type="Edm.String" Nullable="false"/>
                <Property Name="City" Type="Edm.String"/>
              </EntityType>
              <EntityContainer Name="DemoContainer">
                <EntitySet Name="People" EntityType="Demo.Person"/>
              </EntityContainer>
            </Schema>
          </edmx:DataServices>
        </edmx:Edmx>
        """;

    private const string HarborOpenRpc = """
        {
          "openrpc": "1.3.2",
          "info": { "title": "Harbor", "version": "1.0.0" },
          "methods": [ { "name": "ship.get", "params": [ { "name": "id", "schema": { "type": "integer" } } ] } ]
        }
        """;

    private const string LightsAsyncApi = """
        asyncapi: 3.0.0
        info:
          title: Lights
          version: 1.0.0
        servers:
          production:
            host: 'broker.local:1883'
            protocol: mqtt
        channels:
          lightTurnOn:
            address: 'light/turn/on'
            messages:
              turnOn:
                payload:
                  type: object
        operations:
          sendTurnOn:
            action: send
            channel:
              $ref: '#/channels/lightTurnOn'
        """;

    private readonly TempUserRoot _storage = new("uploaded-schema-kinds");

    public void Dispose()
    {
        _storage.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void Store(string kind, string text, string name)
        => SchemaUploadStore.AddBytes(kind, Encoding.UTF8.GetBytes(text), name);

    private static Task<BowireDiscoveryProbeResult> ProbeAsync(BowireProtocolRegistry registry, string url)
        => BowireDiscoveryProbe.RunAsync(
            registry, url, pluginHint: null, showInternalServices: false,
            TimeSpan.FromSeconds(30), ct: TestContext.Current.CancellationToken);

    [Fact]
    public async Task Every_Stored_Kind_Is_Listed_Under_Its_Own_Plugin()
    {
        Store("wsdl", CalculatorWsdl, "calculator.wsdl");
        Store("edmx", PeopleEdmx, "people.edmx");
        Store("openrpc", HarborOpenRpc, "harbor.json");
        Store("asyncapi", LightsAsyncApi, "lights.yaml");

        using var soap = new BowireSoapProtocol();
        using var odata = new BowireODataProtocol();
        using var jsonRpc = new BowireJsonRpcProtocol();
        var registry = new BowireProtocolRegistry();
        registry.Register(soap);
        registry.Register(odata);
        registry.Register(jsonRpc);
        registry.Register(new BowireAsyncApiProtocol());

        // No URL: nothing is probed, and what is stored is the whole answer.
        var probe = await ProbeAsync(registry, "");

        var listed = probe.Services.ToDictionary(s => s.Name, s => s.Source);
        Assert.Equal("soap", listed["Calculator"]);
        Assert.Equal("odata", listed["People"]);
        Assert.Equal("jsonrpc", listed["Methods"]);
        Assert.Equal("asyncapi", listed["lightTurnOn"]);
        Assert.All(probe.Services, s => Assert.True(s.IsUploaded));
    }

    [Fact]
    public async Task A_Kind_Whose_Plugin_Is_Not_Loaded_Stays_Stored_And_Lists_Nothing()
    {
        Store("wsdl", CalculatorWsdl, "calculator.wsdl");
        using var odata = new BowireODataProtocol();
        var registry = new BowireProtocolRegistry();
        registry.Register(odata);

        var probe = await ProbeAsync(registry, "");

        Assert.Empty(probe.Services);
        Assert.True(SchemaUploadStore.Has("wsdl"));
    }

    [Fact]
    public async Task A_Soap_Call_Takes_Its_SoapAction_And_Namespace_From_The_Uploaded_Wsdl()
    {
        Store("wsdl", CalculatorWsdl, "calculator.wsdl");
        string? action = null, body = null;
        var wsdlRequests = 0;
        await using var host = await PluginTestHost.StartAsync(app =>
        {
            app.MapGet("/Calculator.asmx", () => { wsdlRequests++; return Results.NotFound(); });
            app.MapPost("/Calculator.asmx", async (HttpContext ctx) =>
            {
                action = ctx.Request.Headers["SOAPAction"].ToString();
                using var reader = new StreamReader(ctx.Request.Body);
                body = await reader.ReadToEndAsync(ctx.RequestAborted);
                ctx.Response.ContentType = "text/xml";
                await ctx.Response.WriteAsync(
                    """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><AddResponse><r>3</r></AddResponse></s:Body></s:Envelope>""");
            });
        });

        using var soap = new BowireSoapProtocol();
        var result = await soap.InvokeAsync(
            host.BaseUrl + "/Calculator.asmx", "Calculator", "Add",
            ["<a>1</a><b>2</b>"], showInternalServices: false, metadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal("\"http://example.com/calc/Add\"", action);
        // Document style: the body element is qualified by the schema
        // namespace of the input element, not the WSDL's own.
        Assert.Contains("http://example.com/calc/schema", body, StringComparison.Ordinal);
        // The service has no ?wsdl, and with the upload it is not asked.
        Assert.Equal(0, wsdlRequests);
    }

    [Fact]
    public async Task An_OData_Call_Quotes_A_String_Key_It_Knows_Only_From_The_Uploaded_Model()
    {
        Store("edmx", PeopleEdmx, "people.edmx");
        string? requested = null;
        await using var host = await PluginTestHost.StartAsync(app =>
            app.Map("/odata/{**rest}", (HttpContext ctx) =>
            {
                requested = ctx.Request.Path.Value;
                return Results.Json(new { UserName = "ada", City = "London" });
            }));

        // Nothing listed the model in this process: the call comes first, as
        // it does from a flow or the command line.
        using var odata = new BowireODataProtocol();
        await odata.InvokeAsync(
            host.BaseUrl + "/odata", "People", "odata/People/GET_BY_KEY",
            ["""{"UserName":"ada"}"""], showInternalServices: false, metadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("/odata/People('ada')", requested);
    }

    [Fact]
    public async Task An_Uploaded_OpenRpc_Document_Lists_The_Methods_Of_A_Server_Without_Rpc_Discover()
    {
        Store("openrpc", HarborOpenRpc, "harbor.json");
        await using var host = await PluginTestHost.StartAsync(app =>
            app.MapPost("/rpc", async (HttpContext ctx) =>
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(
                    """{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Method not found"}}""");
            }));

        using var jsonRpc = new BowireJsonRpcProtocol();
        var registry = new BowireProtocolRegistry();
        registry.Register(jsonRpc);
        var url = host.BaseUrl + "/rpc";

        var probe = await ProbeAsync(registry, url);

        // The server's own answer is an empty "Methods"; the upload's takes
        // its place rather than standing beside it.
        var methods = Assert.Single(probe.Services);
        Assert.Equal("Methods", methods.Name);
        Assert.True(methods.IsUploaded);
        Assert.Equal(url, methods.OriginUrl);
        Assert.Equal(["ship.get"], methods.Methods.Select(m => m.Name));
    }

    [Fact]
    public async Task A_Call_To_An_Uploaded_AsyncApi_Document_Finds_It_Without_A_Listing_First()
    {
        Store("asyncapi", LightsAsyncApi, "lights.yaml");

        // A fresh plugin: nothing has read the document in this instance.
        var asyncApi = new BowireAsyncApiProtocol();
        var result = await asyncApi.InvokeAsync(
            "lights.yaml", "lightTurnOn", "sendTurnOn", ["{}"],
            showInternalServices: false, metadata: null, TestContext.Current.CancellationToken);

        // What is left to go wrong is past the document: no wire plugin is
        // loaded here to carry the message.
        Assert.DoesNotContain(
            "has not been discovered", result.Metadata.GetValueOrDefault("error") ?? "", StringComparison.Ordinal);
        Assert.NotEmpty(result.Metadata.GetValueOrDefault("error") ?? "");
    }

    [Fact]
    public async Task Without_The_Upload_The_Same_AsyncApi_Call_Asks_For_A_Discovery()
    {
        var asyncApi = new BowireAsyncApiProtocol();
        var result = await asyncApi.InvokeAsync(
            "lights.yaml", "lightTurnOn", "sendTurnOn", ["{}"],
            showInternalServices: false, metadata: null, TestContext.Current.CancellationToken);

        Assert.Contains(
            "has not been discovered", result.Metadata.GetValueOrDefault("error") ?? "", StringComparison.Ordinal);
    }
}
