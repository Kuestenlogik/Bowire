// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Sse;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Tests;

public sealed class BowireSseExtensionsTests : IDisposable
{
    public BowireSseExtensionsTests()
    {
        BowireSseProtocol.ClearRegisteredEndpoints();
    }

    public void Dispose()
    {
        BowireSseProtocol.ClearRegisteredEndpoints();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AddBowireSseEndpoint_Registers_Endpoint_With_All_Fields()
    {
        await using var app = WebApplication.CreateBuilder(
            new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory }).Build();

        var returned = app.AddBowireSseEndpoint(
            "/events/ticker", "Ticker", "Live ticks", "price,volume");

        Assert.Same(app, returned);

        var registered = Assert.Single(BowireSseProtocol.RegisteredEndpoints);
        Assert.Equal("/events/ticker", registered.Path);
        Assert.Equal("Ticker", registered.Name);
        Assert.Equal("Live ticks", registered.Description);
        Assert.Equal("price,volume", registered.EventTypes);
    }

    [Fact]
    public async Task AddBowireSseEndpoint_Optional_Fields_Default_To_Null()
    {
        await using var app = WebApplication.CreateBuilder(
            new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory }).Build();

        app.AddBowireSseEndpoint("/events/heartbeat", "Heartbeat");

        var registered = Assert.Single(BowireSseProtocol.RegisteredEndpoints);
        Assert.Equal("/events/heartbeat", registered.Path);
        Assert.Equal("Heartbeat", registered.Name);
        Assert.Null(registered.Description);
        Assert.Null(registered.EventTypes);
    }

    [Fact]
    public async Task AddBowireSseEndpoint_Multiple_Calls_Registers_Each()
    {
        await using var app = WebApplication.CreateBuilder(
            new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory }).Build();

        app.AddBowireSseEndpoint("/events/a", "A");
        app.AddBowireSseEndpoint("/events/b", "B");
        app.AddBowireSseEndpoint("/events/c", "C");

        Assert.Equal(3, BowireSseProtocol.RegisteredEndpoints.Count);
        Assert.Equal(["/events/a", "/events/b", "/events/c"],
            BowireSseProtocol.RegisteredEndpoints.Select(e => e.Path).ToArray());
    }
}
