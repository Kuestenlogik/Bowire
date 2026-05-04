// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for <see cref="BowireEndpointHelpers"/>: the small static
/// utility surface shared by every per-feature endpoint extension class.
/// Covers the helpers that don't need a live <c>WebApplication</c> —
/// service-list merging, log sanitisation, server-URL resolution, and
/// the registry get/set cache. The query-auth path lives in
/// <see cref="QueryAuthHintsTests"/>.
/// </summary>
public class BowireEndpointHelpersTests
{
    [Fact]
    public void MergeServices_Preserves_Proto_Order_And_Appends_Protocol_Services()
    {
        var protoServices = new List<BowireServiceInfo>
        {
            new("Alpha", "demo", new List<BowireMethodInfo>()),
            new("Beta",  "demo", new List<BowireMethodInfo>()),
        };
        var protocolServices = new List<BowireServiceInfo>
        {
            new("Gamma", "demo", new List<BowireMethodInfo>()),
        };

        var merged = BowireEndpointHelpers.MergeServices(protoServices, protocolServices);

        Assert.Collection(merged,
            s => Assert.Equal("Alpha", s.Name),
            s => Assert.Equal("Beta", s.Name),
            s => Assert.Equal("Gamma", s.Name));
    }

    [Fact]
    public void MergeServices_Drops_Protocol_Service_When_Name_Already_Present_In_Proto()
    {
        // The proto-sourced service is authoritative — when both sides surface
        // the same service name, the proto version wins. This is the contract
        // the discovery endpoint depends on so user uploads don't get
        // shadowed by a stale reflection-discovered shape.
        var protoServices = new List<BowireServiceInfo>
        {
            new("Greeter", "demo", new List<BowireMethodInfo>()) { Source = "proto" },
        };
        var protocolServices = new List<BowireServiceInfo>
        {
            new("Greeter", "demo", new List<BowireMethodInfo>()) { Source = "grpc" },
            new("Other", "demo", new List<BowireMethodInfo>()) { Source = "grpc" },
        };

        var merged = BowireEndpointHelpers.MergeServices(protoServices, protocolServices);

        Assert.Equal(2, merged.Count);
        Assert.Equal("proto", merged[0].Source);
        Assert.Equal("Other", merged[1].Name);
    }

    [Fact]
    public void MergeServices_Empty_Inputs_Return_Empty_List()
    {
        var merged = BowireEndpointHelpers.MergeServices(
            new List<BowireServiceInfo>(),
            new List<BowireServiceInfo>());

        Assert.Empty(merged);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain text", "plain text")]
    [InlineData("with\nnewline", "with_newline")]
    [InlineData("with\rreturn", "with_return")]
    [InlineData("\r\nboth\r\n", "__both__")]
    [InlineData("multi\r\nline\r\ninjection", "multi__line__injection")]
    public void SafeLog_Strips_Control_Characters_For_Log_Forging_Defence(string? input, string expected)
    {
        Assert.Equal(expected, BowireEndpointHelpers.SafeLog(input));
    }

    [Fact]
    public void ResolveServerUrl_Prefers_Explicit_Option_Over_Request_Host()
    {
        var options = new BowireOptions { ServerUrl = "https://override.example.com" };
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("api.local", 8080);

        var url = BowireEndpointHelpers.ResolveServerUrl(options, ctx.Request);

        Assert.Equal("https://override.example.com", url);
    }

    [Fact]
    public void ResolveServerUrl_Falls_Back_To_Request_Scheme_And_Host_When_No_Override()
    {
        var options = new BowireOptions(); // ServerUrl = null
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("api.example.com", 443);

        var url = BowireEndpointHelpers.ResolveServerUrl(options, ctx.Request);

        Assert.Equal("https://api.example.com:443", url);
    }

    [Fact]
    public void ResolveServerUrl_Empty_Override_Falls_Back_To_Request()
    {
        var options = new BowireOptions { ServerUrl = "" };
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("local-host", 5000);

        var url = BowireEndpointHelpers.ResolveServerUrl(options, ctx.Request);

        Assert.Equal("http://local-host:5000", url);
    }

    [Fact]
    public void GetLogger_Returns_NullLogger_When_No_LoggerFactory_Registered()
    {
        // A minimal HttpContext with an empty service provider must still
        // return a usable logger — the helper falls back to NullLogger so
        // endpoints can log unconditionally.
        var services = new ServiceCollection().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };

        var logger = BowireEndpointHelpers.GetLogger(ctx);

        Assert.NotNull(logger);
        // NullLogger swallows everything; this just exercises the call path.
        logger.LogInformation("hello");
    }

    [Fact]
    public void GetLogger_Resolves_From_Registered_LoggerFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = provider };

        var logger = BowireEndpointHelpers.GetLogger(ctx);

        Assert.NotNull(logger);
        logger.LogInformation("hello world");
    }

    [Fact]
    public void GetRegistry_Returns_Set_Value()
    {
        // The cache is process-static. Snapshot the current value, swap in
        // a fresh registry, then restore the original on the way out so we
        // don't poison subsequent tests.
        var original = BowireEndpointHelpers.GetRegistry();
        try
        {
            var custom = new BowireProtocolRegistry();
            BowireEndpointHelpers.SetRegistry(custom);

            Assert.Same(custom, BowireEndpointHelpers.GetRegistry());
        }
        finally
        {
            BowireEndpointHelpers.SetRegistry(original);
        }
    }
}
