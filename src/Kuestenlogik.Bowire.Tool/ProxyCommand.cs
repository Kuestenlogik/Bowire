// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// <c>bowire proxy</c> — Tier-3 intercepting proxy. HTTP traffic is
/// captured plain; HTTPS traffic is intercepted via on-the-fly leaf
/// cert minting once the operator installs the Bowire CA into the
/// local trust store. Stage C will add the workbench "Proxy" tab
/// that consumes the API surface this CLI exposes.
/// </summary>
internal static class ProxyCommand
{
    internal sealed class ProxyOptions
    {
        public int Port { get; init; } = 8888;
        public int ApiPort { get; init; } = 8889;
        public int Capacity { get; init; } = 1000;
        /// <summary>When false, CONNECT tunnels are rejected with 501 instead of being MITM-intercepted.</summary>
        public bool MitmHttps { get; init; } = true;
        /// <summary>Override the CA storage directory (default: <c>~/.bowire</c>).</summary>
        public string? CaDir { get; init; }
        /// <summary>When set, copy the public CA cert here + exit immediately.</summary>
        public string? ExportCa { get; init; }
    }

    public static async Task<int> RunAsync(ProxyOptions options, TextWriter? stdout = null, TextWriter? stderr = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var io = CommandIo.Resolve(stdout, stderr);

        // --export-ca short-circuit: load (or create) the CA, copy the
        // public cert to the operator-chosen path, exit.
        if (!string.IsNullOrEmpty(options.ExportCa))
        {
            using var caForExport = BowireProxyCertificateAuthority.LoadOrCreate(options.CaDir);
            caForExport.ExportPublicCertificate(options.ExportCa);
            await io.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"bowire proxy: CA certificate exported to {options.ExportCa}")).ConfigureAwait(false);
            await io.Out.WriteLineAsync("Install this file into your OS / browser trust store to let bowire intercept HTTPS traffic without warnings.").ConfigureAwait(false);
            return 0;
        }

        var store = new CapturedFlowStore(options.Capacity);
        BowireProxyCertificateAuthority? ca = null;
        if (options.MitmHttps)
        {
            try
            {
                ca = BowireProxyCertificateAuthority.LoadOrCreate(options.CaDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                await io.Err.WriteLineAsync($"bowire proxy: could not initialise CA: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
        }

        await using var proxy = new BowireProxyServer(store, options.Port, ca);

        try
        {
            await proxy.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or InvalidOperationException)
        {
            await io.Err.WriteLineAsync($"bowire proxy: could not bind proxy port {options.Port}: {ex.Message}").ConfigureAwait(false);
            ca?.Dispose();
            return 1;
        }

        // Sidecar HTTP API the workbench's "Proxy" tab polls/streams.
        var apiBuilder = WebApplication.CreateSlimBuilder();
        apiBuilder.Logging.ClearProviders();
        apiBuilder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, options.ApiPort, l => l.Protocols = HttpProtocols.Http1);
        });
        // `await using` so a throw between Build() and the graceful
        // shutdown path below still releases Kestrel + DI container
        // (cs/dispose-not-called-on-throw).
        await using var api = apiBuilder.Build();
        api.MapBowireProxyEndpoints("", store);

        try
        {
            await api.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or InvalidOperationException)
        {
            await io.Err.WriteLineAsync($"bowire proxy: could not bind workbench-API port {options.ApiPort}: {ex.Message}").ConfigureAwait(false);
            await proxy.StopAsync(CancellationToken.None).ConfigureAwait(false);
            ca?.Dispose();
            return 1;
        }

        await io.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"bowire proxy: intercepting on http://127.0.0.1:{proxy.Port}")).ConfigureAwait(false);
        await io.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"bowire proxy: workbench API on http://127.0.0.1:{options.ApiPort}/api/proxy/flows  (capacity {options.Capacity})")).ConfigureAwait(false);
        if (ca is not null)
        {
            await io.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"bowire proxy: HTTPS interception ENABLED (CA at {ca.CaCertPath}; install into trust store to avoid client warnings).")).ConfigureAwait(false);
        }
        else
        {
            await io.Out.WriteLineAsync("bowire proxy: HTTPS interception DISABLED (--no-mitm). CONNECT requests are rejected with 501.").ConfigureAwait(false);
        }
        await io.Out.WriteLineAsync("bowire proxy: press Ctrl-C to stop.").ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* graceful stop */ }

        await api.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await proxy.StopAsync(CancellationToken.None).ConfigureAwait(false);
        ca?.Dispose();
        return 0;
    }
}
