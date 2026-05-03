// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Sse;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests the auto-discovery branch of <see cref="SseEndpointDiscovery"/> —
/// scanning <see cref="Microsoft.AspNetCore.Routing.EndpointDataSource"/>
/// instances for endpoints carrying <see cref="SseEndpointAttribute"/> or
/// <c>Produces("text/event-stream")</c> metadata. Built atop a real
/// <see cref="WebApplication"/> so the metadata pipeline is the same one
/// the production discovery path walks.
/// </summary>
public sealed class SseEndpointDiscoveryTests : IDisposable
{
    public SseEndpointDiscoveryTests()
    {
        BowireSseProtocol.ClearRegisteredEndpoints();
    }

    public void Dispose()
    {
        BowireSseProtocol.ClearRegisteredEndpoints();
        GC.SuppressFinalize(this);
    }

    private static async Task<WebApplication> BuildAppWithEndpointsAsync(Action<WebApplication> mapEndpoints)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();

        var app = builder.Build();
        mapEndpoints(app);
        // Start so the endpoint data sources get registered into DI before
        // SseEndpointDiscovery walks IServiceProvider for them.
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task DiscoverAsync_Picks_Up_Endpoints_With_SseEndpointAttribute()
    {
        await using var app = await BuildAppWithEndpointsAsync(a =>
        {
            a.MapGet("/events/scanned", () => "ok")
                .WithMetadata(new SseEndpointAttribute
                {
                    Description = "Scanned ticker",
                    EventType = "tick",
                });
        });

        var protocol = new BowireSseProtocol();
        protocol.Initialize(app.Services);

        var services = await protocol.DiscoverAsync(
            "http://localhost:5000",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        var method = Assert.Single(svc.Methods);
        Assert.Equal("Scanned ticker", method.Name);
        Assert.Equal("SSE/events/scanned", method.FullName);
    }

    [Fact]
    public async Task DiscoverAsync_Picks_Up_Endpoints_With_Produces_EventStream()
    {
        await using var app = await BuildAppWithEndpointsAsync(a =>
        {
            // Produces<T>(string contentType, ...) is the minimal-API
            // builder extension that registers IProducesResponseTypeMetadata.
            a.MapGet("/events/produces", () => "ok")
                .Produces<string>(StatusCodes.Status200OK, "text/event-stream");
        });

        var protocol = new BowireSseProtocol();
        protocol.Initialize(app.Services);

        var services = await protocol.DiscoverAsync(
            "http://localhost:5000",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        var method = Assert.Single(svc.Methods);
        Assert.Equal("SSE/events/produces", method.FullName);
    }

    [Fact]
    public async Task DiscoverAsync_Ignores_Endpoints_Without_Sse_Markers()
    {
        await using var app = await BuildAppWithEndpointsAsync(a =>
        {
            // Plain JSON endpoint — must NOT be picked up.
            a.MapGet("/api/users", () => new { id = 1 });
        });

        var protocol = new BowireSseProtocol();
        protocol.Initialize(app.Services);

        var services = await protocol.DiscoverAsync(
            "http://localhost:5000",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Manual_Registration_Wins_Over_Scanned_Duplicate()
    {
        // Manual registration uses display name "Manual Ticker".
        BowireSseProtocol.RegisterEndpoint(
            new SseEndpointInfo("/events/dup", "Manual Ticker"));

        await using var app = await BuildAppWithEndpointsAsync(a =>
        {
            // Auto-discovered duplicate would name itself after the
            // endpoint's display name; manual entry takes precedence.
            a.MapGet("/events/dup", () => "ok")
                .WithMetadata(new SseEndpointAttribute { Description = "Auto Ticker" });
        });

        var protocol = new BowireSseProtocol();
        protocol.Initialize(app.Services);

        var services = await protocol.DiscoverAsync(
            "http://localhost:5000",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        var method = Assert.Single(services[0].Methods);
        Assert.Equal("Manual Ticker", method.Name);
    }
}
