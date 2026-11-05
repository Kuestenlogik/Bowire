// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Protocol.Soap;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// A SOAP call entered by hand, against a service that serves no WSDL
/// (#996). The workbench's freeform form now sends the call's metadata for
/// every protocol; this holds the other half — that <c>/api/invoke</c>
/// hands it to the SOAP plugin, which addresses the call with it.
/// </summary>
[Collection("BowireProtocolRegistry")]
public sealed class SoapByHandInvokeTests : IDisposable
{
    private static readonly string[] BodyParts = ["<a>1</a><b>2</b>"];

    public void Dispose() => BowireEndpointHelpers.ResetRegistry();

    [Fact]
    public async Task Action_And_Namespace_From_The_Metadata_Reach_A_Service_Without_Wsdl()
    {
        var ct = TestContext.Current.CancellationToken;

        // The SOAP service: answers every POST, has no ?wsdl, and records
        // what it was sent.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var service = builder.Build();
        string? soapAction = null, envelope = null;
        service.Run(async ctx =>
        {
            soapAction = ctx.Request.Headers["SOAPAction"].ToString();
            using var reader = new StreamReader(ctx.Request.Body);
            envelope = await reader.ReadToEndAsync(ctx.RequestAborted);
            ctx.Response.ContentType = "text/xml";
            await ctx.Response.WriteAsync(
                "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><AddResponse><r>3</r></AddResponse></soap:Body></soap:Envelope>",
                ctx.RequestAborted);
        });
        await service.StartAsync(ct);
        var endpoint = service.Urls.First() + "/calc";

        using var soap = new BowireSoapProtocol();
        var registry = new BowireProtocolRegistry();
        registry.Register(soap);
        BowireEndpointHelpers.SetRegistry(registry);

        using var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapBowireInvokeEndpoints(new BowireOptions(), basePath: string.Empty));
                })
                .ConfigureServices(s => s.AddRouting()))
            .Build();
        await host.StartAsync(ct);

        // What the freeform form posts for a hand-entered SOAP call: no
        // discovery happened, so service and method are typed names.
        var request = JsonSerializer.Serialize(new
        {
            protocol = "soap",
            service = "Calculator",
            method = "Add",
            messages = BodyParts,
            metadata = new Dictionary<string, string>
            {
                ["soap_action"] = "urn:calc/Add",
                ["target_namespace"] = "urn:calc",
            },
        });
        using var content = new StringContent(request, Encoding.UTF8, "application/json");
        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/invoke?serverUrl=" + Uri.EscapeDataString(endpoint), UriKind.Relative), content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("\"urn:calc/Add\"", soapAction);
        Assert.NotNull(envelope);
        Assert.Contains("urn:calc", envelope, StringComparison.Ordinal);
        Assert.Contains("Add", envelope, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"OK\"", body, StringComparison.Ordinal);
    }
}
