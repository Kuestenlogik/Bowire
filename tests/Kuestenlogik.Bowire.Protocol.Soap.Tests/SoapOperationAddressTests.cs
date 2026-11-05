// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.Soap.Tests;

/// <summary>
/// #1000 — a call to a discovered operation carries the operation's
/// SOAPAction and the namespace of its body element, without the user typing
/// them in. They used to come from the call's metadata only, so a discovered
/// call went out with <c>SOAPAction: ""</c> and an unqualified body.
/// </summary>
public sealed class SoapOperationAddressTests
{
    // Document/literal: the body element is the part's element, qualified by
    // the schema namespace — which differs from the WSDL's own here, so the
    // test tells the two apart.
    private const string DocumentWsdl = """
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                     xmlns:tns="http://example.com/calc/wsdl"
                     xmlns:s="http://example.com/calc/schema"
                     targetNamespace="http://example.com/calc/wsdl">
          <message name="AddIn"><part name="parameters" element="s:Add"/></message>
          <message name="SubIn"><part name="a" type="s:int"/></message>
          <portType name="Calculator">
            <operation name="Add"><input message="tns:AddIn"/></operation>
            <operation name="Subtract"><input message="tns:SubIn"/></operation>
          </portType>
          <binding name="CalculatorSoap" type="tns:Calculator">
            <operation name="Add"><soap:operation soapAction="http://example.com/calc/Add"/></operation>
            <operation name="Subtract">
              <soap:operation soapAction="http://example.com/calc/Subtract"/>
              <input><soap:body use="encoded" namespace="urn:calc-rpc"/></input>
            </operation>
          </binding>
        </definitions>
        """;

    [Fact]
    public void Document_Style_Takes_The_Namespace_Of_The_Input_Element()
    {
        var addresses = SoapOperationAddresses.Parse(XDocument.Parse(DocumentWsdl));

        var add = addresses["Calculator/Add"];
        Assert.Equal("http://example.com/calc/Add", add.SoapAction);
        Assert.Equal("http://example.com/calc/schema", add.TargetNamespace);
    }

    [Fact]
    public void Rpc_Style_Takes_The_Namespace_Of_Soap_Body()
    {
        var addresses = SoapOperationAddresses.Parse(XDocument.Parse(DocumentWsdl));

        Assert.Equal("urn:calc-rpc", addresses["Calculator/Subtract"].TargetNamespace);
    }

    [Fact]
    public void Without_Either_The_Wsdl_Namespace_Applies()
    {
        const string wsdl = """
            <definitions xmlns="http://schemas.xmlsoap.org/wsdl/" xmlns:tns="urn:w" targetNamespace="urn:w">
              <message name="PingIn"><part name="a" type="tns:string"/></message>
              <portType name="P"><operation name="Ping"><input message="tns:PingIn"/></operation></portType>
            </definitions>
            """;

        var ping = SoapOperationAddresses.Parse(XDocument.Parse(wsdl))["P/Ping"];

        Assert.Equal("", ping.SoapAction);
        Assert.Equal("urn:w", ping.TargetNamespace);
    }

    [Fact]
    public void An_Operation_Is_Found_By_Port_Type_Or_Alone_When_It_Is_Unique()
    {
        var addresses = new Dictionary<string, SoapOperationAddress>
        {
            ["A/Ping"] = new("a", "urn:a"),
            ["B/Ping"] = new("b", "urn:b"),
            ["A/Add"] = new("add", "urn:a"),
        };

        Assert.Equal("b", SoapOperationAddresses.Find(addresses, "B", "Ping")!.SoapAction);
        Assert.Equal("add", SoapOperationAddresses.Find(addresses, null, "Add")!.SoapAction);
        Assert.Null(SoapOperationAddresses.Find(addresses, "Unknown", "Ping"));
        Assert.Null(SoapOperationAddresses.Find(addresses, null, "Missing"));
    }

    /// <summary>A service that serves its WSDL at <c>?wsdl</c> and records every call it gets.</summary>
    private sealed class FakeService : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public int WsdlFetches;
        public string? SoapAction;
        public string? Envelope;

        private FakeService(WebApplication app) => _app = app;

        public string Url => _app.Urls.First() + "/calc";

        public static async Task<FakeService> StartAsync(string? wsdl)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var service = new FakeService(app);
            app.Run(async ctx =>
            {
                if (HttpMethods.IsGet(ctx.Request.Method))
                {
                    Interlocked.Increment(ref service.WsdlFetches);
                    if (wsdl is null) { ctx.Response.StatusCode = 404; return; }
                    ctx.Response.ContentType = "text/xml";
                    await ctx.Response.WriteAsync(wsdl, ctx.RequestAborted);
                    return;
                }
                service.SoapAction = ctx.Request.Headers["SOAPAction"].ToString();
                using var reader = new StreamReader(ctx.Request.Body);
                service.Envelope = await reader.ReadToEndAsync(ctx.RequestAborted);
                ctx.Response.ContentType = "text/xml";
                await ctx.Response.WriteAsync(
                    "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><AddResponse/></soap:Body></soap:Envelope>",
                    ctx.RequestAborted);
            });
            await app.StartAsync(TestContext.Current.CancellationToken);
            return service;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    [Fact]
    public async Task A_Discovered_Call_Carries_Action_And_Namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var service = await FakeService.StartAsync(DocumentWsdl);
        using var plugin = new BowireSoapProtocol();
        await plugin.DiscoverAsync(service.Url + "?wsdl", false, ct);

        // The workbench sends the discovered method's name and its port type.
        var result = await plugin.InvokeAsync(service.Url + "?wsdl", "Calculator", "Add", ["<a>1</a>"], false, null, ct);

        Assert.Equal("OK", result.Status);
        Assert.Equal("\"http://example.com/calc/Add\"", service.SoapAction);
        Assert.Contains("<Add xmlns=\"http://example.com/calc/schema\"", service.Envelope, StringComparison.Ordinal);
        Assert.Equal(1, service.WsdlFetches);
    }

    [Fact]
    public async Task A_Call_Without_Prior_Discovery_Loads_The_Wsdl_Once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var service = await FakeService.StartAsync(DocumentWsdl);
        using var plugin = new BowireSoapProtocol();

        // The CLI, a flow or a recording invokes without discovering first.
        await plugin.InvokeAsync(service.Url, "Calculator", "Add", ["<a>1</a>"], false, null, ct);
        await plugin.InvokeAsync(service.Url, "Calculator", "Add", ["<a>2</a>"], false, null, ct);

        Assert.Equal("\"http://example.com/calc/Add\"", service.SoapAction);
        Assert.Equal(1, service.WsdlFetches);
    }

    [Fact]
    public async Task What_The_Metadata_Says_Wins_Over_The_Wsdl()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var service = await FakeService.StartAsync(DocumentWsdl);
        using var plugin = new BowireSoapProtocol();

        await plugin.InvokeAsync(service.Url, "Calculator", "Add", ["<a>1</a>"], false,
            new Dictionary<string, string> { ["soap_action"] = "urn:mine", ["target_namespace"] = "urn:mine-ns" }, ct);

        Assert.Equal("\"urn:mine\"", service.SoapAction);
        Assert.Contains("<Add xmlns=\"urn:mine-ns\"", service.Envelope, StringComparison.Ordinal);
        Assert.Equal(0, service.WsdlFetches);
    }

    [Fact]
    public async Task A_Service_Without_A_Wsdl_Gets_What_Was_Typed_And_Is_Asked_Once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var service = await FakeService.StartAsync(wsdl: null);
        using var plugin = new BowireSoapProtocol();

        await plugin.InvokeAsync(service.Url, "SOAP", "Add", ["<a>1</a>"], false, null, ct);
        var result = await plugin.InvokeAsync(service.Url, "SOAP", "Add", ["<a>1</a>"], false, null, ct);

        Assert.Equal("OK", result.Status);
        Assert.Equal("\"\"", service.SoapAction);
        Assert.Equal(1, service.WsdlFetches);
    }
}
