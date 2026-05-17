// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// <c>bowire proxy</c> — Tier-3 intercepting proxy. Stage A:
/// plain-HTTP forward proxy + a sidecar HTTP API serving captured
/// flows to the workbench. Stage B will add HTTPS interception
/// via on-the-fly leaf-cert minting; Stage C adds the workbench
/// "Proxy" tab that consumes the API surface defined here.
/// </summary>
internal static class ProxyCommand
{
    internal sealed class ProxyOptions
    {
        public int Port { get; init; } = 8888;
        public int ApiPort { get; init; } = 8889;
        public int Capacity { get; init; } = 1000;
    }

    public static async Task<int> RunAsync(ProxyOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var store = new CapturedFlowStore(options.Capacity);
        await using var proxy = new BowireProxyServer(store, options.Port);

        try
        {
            await proxy.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"bowire proxy: could not bind proxy port {options.Port}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        // Sidecar HTTP API the workbench's "Proxy" tab will poll/stream.
        var apiBuilder = WebApplication.CreateSlimBuilder();
        apiBuilder.Logging.ClearProviders();
        apiBuilder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, options.ApiPort, l => l.Protocols = HttpProtocols.Http1);
        });
        var api = apiBuilder.Build();
        api.MapBowireProxyEndpoints("", store);

        try
        {
            await api.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"bowire proxy: could not bind workbench-API port {options.ApiPort}: {ex.Message}").ConfigureAwait(false);
            await proxy.StopAsync(CancellationToken.None).ConfigureAwait(false);
            return 1;
        }

        await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"bowire proxy: intercepting on http://127.0.0.1:{proxy.Port}  (Stage A: plain HTTP; HTTPS lands in Stage B)")).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"bowire proxy: workbench API on http://127.0.0.1:{options.ApiPort}/api/proxy/flows  (capacity {options.Capacity})")).ConfigureAwait(false);
        await Console.Out.WriteLineAsync("bowire proxy: press Ctrl-C to stop.").ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* graceful stop */ }

        await api.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await proxy.StopAsync(CancellationToken.None).ConfigureAwait(false);
        return 0;
    }
}
