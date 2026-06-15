// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Protocol.Soap;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Kuestenlogik.Bowire.IntegrationTests.Soap;

/// <summary>
/// Drives <see cref="BowireSoapProtocol"/> end-to-end against an
/// in-process minimal SOAP service. The fixture serves a WSDL on
/// <c>?wsdl</c> and accepts SOAP-envelope POSTs at the same path; this
/// is the part of the plugin that's pure-unit-test-out-of-reach
/// (HTTP I/O, status-handling, response-body parsing, Fault → Status
/// translation).
/// </summary>
public sealed class SoapPluginIntegrationTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string _baseUrl = "";

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _app = builder.Build();
        _app.MapGet("/svc", async (HttpContext ctx) =>
        {
            if (!ctx.Request.Query.ContainsKey("wsdl"))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            ctx.Response.ContentType = "text/xml";
            await ctx.Response.WriteAsync(WsdlText("http://localhost/svc"));
        });

        _app.MapPost("/svc", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            ctx.Response.ContentType = "text/xml";
            ctx.Response.StatusCode = body.Contains("Boom", StringComparison.Ordinal)
                ? StatusCodes.Status500InternalServerError
                : StatusCodes.Status200OK;

            if (body.Contains("Boom", StringComparison.Ordinal))
            {
                await ctx.Response.WriteAsync("""
                    <?xml version="1.0"?>
                    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                      <soap:Body>
                        <soap:Fault>
                          <faultcode>soap:Server</faultcode>
                          <faultstring>boom from fixture</faultstring>
                        </soap:Fault>
                      </soap:Body>
                    </soap:Envelope>
                    """);
                return;
            }

            await ctx.Response.WriteAsync("""
                <?xml version="1.0"?>
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body>
                    <EchoResponse xmlns="http://example.com/echo">
                      <result>ok</result>
                    </EchoResponse>
                  </soap:Body>
                </soap:Envelope>
                """);
        });

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        _baseUrl = addresses.First() + "/svc";
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task DiscoverAsync_Fetches_Wsdl_And_Surfaces_Operations()
    {
        using var p = new BowireSoapProtocol();
        var services = await p.DiscoverAsync(_baseUrl, showInternalServices: false,
            TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        Assert.Equal("Echo", svc.Name);
        Assert.Contains(svc.Methods, m => m.Name == "Echo");
        Assert.Contains(svc.Methods, m => m.Name == "Boom");
    }

    [Fact]
    public async Task DiscoverAsync_Returns_Empty_For_404()
    {
        using var p = new BowireSoapProtocol();
        // Hit a path the fixture doesn't serve — _baseUrl ends in /svc,
        // this points at /missing which returns the default 404.
        var bad = _baseUrl.Replace("/svc", "/missing", StringComparison.Ordinal);
        var services = await p.DiscoverAsync(bad, showInternalServices: false,
            TestContext.Current.CancellationToken);
        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Returns_Empty_For_Unreachable_Host()
    {
        using var p = new BowireSoapProtocol();
        var services = await p.DiscoverAsync(
            "http://127.0.0.1:1/svc", showInternalServices: false,
            TestContext.Current.CancellationToken);
        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Returns_Empty_For_Non_Xml_Body()
    {
        // Configure the fixture to serve garbage at a different path
        // and confirm BowireSoapProtocol swallows the parse error.
        _app!.MapGet("/garbage", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/xml";
            await ctx.Response.WriteAsync("not xml at all");
        });
        using var p = new BowireSoapProtocol();
        var bad = _baseUrl.Replace("/svc", "/garbage?wsdl", StringComparison.Ordinal);
        var services = await p.DiscoverAsync(bad, showInternalServices: false,
            TestContext.Current.CancellationToken);
        Assert.Empty(services);
    }

    [Fact]
    public async Task InvokeAsync_Roundtrips_Echo_Operation()
    {
        using var p = new BowireSoapProtocol();
        var result = await p.InvokeAsync(
            serverUrl: _baseUrl,
            service: "Echo",
            method: "Echo/Echo",
            jsonMessages: ["<text>hello</text>"],
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                ["target_namespace"] = "http://example.com/echo",
                ["soap_action"] = "http://example.com/echo/Echo",
            },
            ct: TestContext.Current.CancellationToken);
        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("EchoResponse", result.Response);
        Assert.Equal("200", result.Metadata["http_status"]);
        Assert.Equal("1.1", result.Metadata["soap_version"]);
    }

    [Fact]
    public async Task InvokeAsync_Surfaces_Fault_With_Fault_Status()
    {
        using var p = new BowireSoapProtocol();
        var result = await p.InvokeAsync(
            serverUrl: _baseUrl,
            service: "Echo",
            method: "Echo/Boom",
            jsonMessages: ["<text>Boom</text>"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal("Fault", result.Status);
        Assert.Contains("boom from fixture", result.Response);
    }

    [Fact]
    public async Task InvokeAsync_Honours_Endpoint_Url_Metadata_Override()
    {
        using var p = new BowireSoapProtocol();
        // Discovery URL points elsewhere; the override redirects the
        // POST back to our fixture so the result still round-trips.
        var result = await p.InvokeAsync(
            serverUrl: "http://other.invalid/svc",
            service: "Echo",
            method: "Echo/Echo",
            jsonMessages: ["<text>ok</text>"],
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                ["endpoint_url"] = _baseUrl,
                ["soap_action"] = "",
            },
            ct: TestContext.Current.CancellationToken);
        Assert.Equal("OK", result.Status);
    }

    [Fact]
    public async Task InvokeAsync_Uses_Soap_1_2_Content_Type_When_Requested()
    {
        string? capturedContentType = null;
        _app!.MapPost("/svc12", async (HttpContext ctx) =>
        {
            capturedContentType = ctx.Request.ContentType;
            // 1.2 servers don't use SOAPAction header — return a
            // benign empty envelope so the plugin's response path runs.
            ctx.Response.ContentType = "application/soap+xml";
            await ctx.Response.WriteAsync("""
                <?xml version="1.0"?>
                <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope">
                  <soap:Body><Pong/></soap:Body>
                </soap:Envelope>
                """);
        });
        using var p = new BowireSoapProtocol();
        var url12 = _baseUrl.Replace("/svc", "/svc12", StringComparison.Ordinal);
        await p.InvokeAsync(
            serverUrl: url12,
            service: "X",
            method: "X/Ping",
            jsonMessages: [""],
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                ["soap_version"] = "1.2",
                ["soap_action"] = "http://example.com/Ping",
            },
            ct: TestContext.Current.CancellationToken);
        Assert.NotNull(capturedContentType);
        Assert.Contains("application/soap+xml", capturedContentType);
        Assert.Contains("action=\"http://example.com/Ping\"", capturedContentType);
    }

    [Fact]
    public async Task InvokeAsync_Returns_Parse_Error_For_Bad_Server_Url()
    {
        using var p = new BowireSoapProtocol();
        var result = await p.InvokeAsync(
            serverUrl: "", service: "x", method: "x/y",
            jsonMessages: ["<a/>"], showInternalServices: false, metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, result.DurationMs);
        Assert.Contains("Could not parse", result.Status);
    }

    [Fact]
    public async Task InvokeAsync_Returns_Exception_Message_For_Unreachable_Host()
    {
        using var p = new BowireSoapProtocol();
        var result = await p.InvokeAsync(
            serverUrl: "http://127.0.0.1:1/svc",
            service: "x", method: "x/y",
            jsonMessages: ["<a/>"], showInternalServices: false, metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.NotEqual("OK", result.Status);
        // Some flavour of "no connection could be made" — message is
        // OS-localised so we only check that it's not the parse-error
        // sentinel which would mean we never got to the SendAsync.
        Assert.DoesNotContain("Could not parse", result.Status);
    }

    [Fact]
    public async Task InvokeStreamAsync_Returns_Empty_Stream()
    {
        using var p = new BowireSoapProtocol();
        var any = false;
        await foreach (var _ in p.InvokeStreamAsync(_baseUrl, "x", "x/y", [],
            showInternalServices: false, metadata: null,
            ct: TestContext.Current.CancellationToken))
        {
            any = true;
        }
        Assert.False(any);
    }

    [Fact]
    public async Task OpenChannelAsync_Returns_Null()
    {
        using var p = new BowireSoapProtocol();
        var ch = await p.OpenChannelAsync(_baseUrl, "x", "x/y",
            showInternalServices: false, metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Null(ch);
    }

    [Fact]
    public void Initialize_With_Null_Service_Provider_Is_Safe()
    {
        using var p = new BowireSoapProtocol();
        p.Initialize(null);
        // Defaulted HttpClient is still usable; Settings still returns
        // the default-soap-version entry.
        Assert.Single(p.Settings);
    }

    private static string WsdlText(string endpointUrl) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                     xmlns:tns="http://example.com/echo"
                     targetNamespace="http://example.com/echo">
          <message name="EchoRequest"><part name="text" type="xs:string"/></message>
          <message name="EchoResponse"><part name="result" type="xs:string"/></message>
          <message name="BoomRequest"><part name="text" type="xs:string"/></message>
          <message name="BoomResponse"><part name="result" type="xs:string"/></message>
          <portType name="Echo">
            <operation name="Echo">
              <input message="tns:EchoRequest"/>
              <output message="tns:EchoResponse"/>
            </operation>
            <operation name="Boom">
              <input message="tns:BoomRequest"/>
              <output message="tns:BoomResponse"/>
            </operation>
          </portType>
          <binding name="EchoSoap" type="tns:Echo">
            <soap:binding style="document" transport="http://schemas.xmlsoap.org/soap/http"/>
            <operation name="Echo">
              <soap:operation soapAction="http://example.com/echo/Echo"/>
            </operation>
            <operation name="Boom">
              <soap:operation soapAction="http://example.com/echo/Boom"/>
            </operation>
          </binding>
          <service name="EchoService">
            <port name="EchoSoap" binding="tns:EchoSoap">
              <soap:address location="{endpointUrl}"/>
            </port>
          </service>
        </definitions>
        """;
}
