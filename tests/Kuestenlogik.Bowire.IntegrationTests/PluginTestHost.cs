// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        // Quiet ASP.NET startup logging in tests
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseWebSockets();
        configure(app);

        await app.StartAsync(TestContext.Current.CancellationToken);

        return new PluginTestHost(app, url);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TestContext.Current.CancellationToken);
        await _app.DisposeAsync();
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
