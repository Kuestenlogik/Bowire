// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Spins up a real Kestrel host on a free localhost port. Used by the
/// per-plugin integration tests when the plugin needs an actual TCP socket
/// (rather than the in-memory <c>TestServer</c> pipe). The classic case is
/// the WebSocket plugin: <c>ClientWebSocket</c> opens a real connection,
/// so we can't route it through TestServer.
/// </summary>
internal sealed class PluginTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string BaseUrl { get; }

    private PluginTestHost(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public static async Task<PluginTestHost> StartAsync(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateBuilder();
        // Bind to port 0 and let Kestrel pick a free port atomically.
        // The old "open a TcpListener on :0, read the port, close it,
        // then hand the number to UseUrls" dance had a TOCTOU race:
        // under xUnit's parallel execution a second host could grab the
        // same "free" port in the gap before Kestrel bound it, which
        // surfaced on CI as "address already in use". Reading the bound
        // address back after StartAsync removes the gap entirely.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // Quiet ASP.NET startup logging in tests
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseWebSockets();
        configure(app);

        await app.StartAsync(TestContext.Current.CancellationToken);

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel did not report a bound address.");

        return new PluginTestHost(app, address);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TestContext.Current.CancellationToken);
        await _app.DisposeAsync();
    }
}
