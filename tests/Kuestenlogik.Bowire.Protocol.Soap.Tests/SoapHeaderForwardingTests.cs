// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Protocol.Soap;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.Soap.Tests;

/// <summary>
/// SOAP sent none of the call's metadata, so no auth helper reached a SOAP
/// service (#679). Now the headers go out — without Bowire's markers and
/// without the four SOAP settings, which are not headers.
/// </summary>
public sealed class SoapHeaderForwardingTests
{
    [Fact]
    public async Task Metadata_Headers_Reach_The_Service_Settings_And_Markers_Do_Not()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        string? seen = null;
        app.Run(async ctx =>
        {
            seen = string.Join(",", ctx.Request.Headers.Keys);
            ctx.Response.ContentType = "text/xml";
            await ctx.Response.WriteAsync(
                "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><PingResponse/></soap:Body></soap:Envelope>",
                ctx.RequestAborted);
        });
        await app.StartAsync(ct);

        using var plugin = new BowireSoapProtocol();
        var result = await plugin.InvokeAsync(
            app.Urls.First() + "/ping", "Pinger", "Pinger/Ping", ["<Ping/>"], false,
            new Dictionary<string, string>
            {
                ["Authorization"] = "Basic YWxpY2U6cHc=",
                ["soap_action"] = "urn:ping",
                ["__bowireHttpAuth__"] = "{\"scheme\":\"digest\",\"user\":\"alice\",\"password\":\"pw\"}",
            },
            ct);

        Assert.NotNull(seen);
        Assert.Contains("Authorization", seen, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("soap_action", seen, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("__bowire", seen, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("OK", result.Status);
    }
}
