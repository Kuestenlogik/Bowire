// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Interceptor;
using Kuestenlogik.Bowire.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// <c>bowire interceptor</c> — Phase C of the #153 interceptor track
/// (#307). Boots Bowire as a reverse-proxy in front of an upstream
/// service: the client points at Bowire's edge listener, every request
/// is forwarded upstream and captured into the same
/// <see cref="InterceptedFlowStore"/> the embedded middleware uses.
/// </summary>
/// <remarks>
/// <para>
/// Same shape as <see cref="ProxyCommand"/>: edge listener + sidecar
/// API the workbench's "Intercepted" rail talks to. HTTPS on the edge
/// reuses #36's MITM CA via
/// <see cref="BowireProxyCertificateAuthority"/> — operators install
/// the CA into their trust store and Bowire mints a leaf for the host
/// the listener answers under (loopback by default).
/// </para>
/// <para>
/// Composes with the embedded middleware: an operator who already has
/// the workbench open at <c>http://localhost:5080</c> can keep using
/// the same "Intercepted" rail — the standalone <c>bowire interceptor</c>
/// hosts its own sidecar API on a separate port, so two workbench
/// instances can talk to two interceptors without colliding.
/// </para>
/// </remarks>
internal static class InterceptorCommand
{
    internal sealed class InterceptorOptions
    {
        public string Upstream { get; init; } = "";
        public string Listen { get; init; } = "127.0.0.1:0";
        public int ApiPort { get; init; } = 5089;
        public int Capacity { get; init; } = 1000;
        public int MaxBodyBytes { get; init; } = 1024 * 1024;
        public bool AllowSelfSignedUpstream { get; init; }
        public string? CaDir { get; init; }
        public bool Tls { get; init; }
        public string? TlsHost { get; init; }
    }

    public static async Task<int> RunAsync(InterceptorOptions options,
        TextWriter? stdout = null, TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var io = CommandIo.Resolve(stdout, stderr);

        if (string.IsNullOrWhiteSpace(options.Upstream))
        {
            await io.Err.WriteLineAsync("bowire interceptor: --upstream is required (e.g. https://api.example.com).").ConfigureAwait(false);
            return 64;
        }
        if (!Uri.TryCreate(options.Upstream, UriKind.Absolute, out var upstreamUri)
            || (upstreamUri.Scheme != Uri.UriSchemeHttp && upstreamUri.Scheme != Uri.UriSchemeHttps))
        {
            await io.Err.WriteLineAsync($"bowire interceptor: '{options.Upstream}' is not a valid http(s):// URL.").ConfigureAwait(false);
            return 64;
        }
        if (!TryParseListen(options.Listen, out var listenAddress, out var listenPort, out var listenErr))
        {
            await io.Err.WriteLineAsync($"bowire interceptor: --listen value '{options.Listen}' is invalid: {listenErr}").ConfigureAwait(false);
            return 64;
        }

        // -------- optional TLS termination on the edge --------
        BowireProxyCertificateAuthority? ca = null;
        System.Security.Cryptography.X509Certificates.X509Certificate2? edgeCert = null;
        if (options.Tls)
        {
            try
            {
                ca = BowireProxyCertificateAuthority.LoadOrCreate(options.CaDir);
                var host = string.IsNullOrWhiteSpace(options.TlsHost) ? listenAddress.ToString() : options.TlsHost!;
                edgeCert = ca.GetOrMintLeaf(host);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                await io.Err.WriteLineAsync($"bowire interceptor: could not initialise CA / mint leaf: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
        }

        var store = new InterceptedFlowStore(options.Capacity);
        var hostOpts = new BowireReverseProxyHostOptions
        {
            Upstream = upstreamUri,
            ListenAddress = listenAddress,
            ListenPort = listenPort,
            Capacity = options.Capacity,
            MaxBodyBytes = options.MaxBodyBytes,
            Store = store,
            AllowSelfSignedUpstream = options.AllowSelfSignedUpstream,
            ServerCertificate = edgeCert,
        };

        await using var edge = BowireReverseProxyHost.Create(hostOpts);
        try
        {
            await edge.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or InvalidOperationException)
        {
            await io.Err.WriteLineAsync($"bowire interceptor: could not bind edge port {listenPort}: {ex.Message}").ConfigureAwait(false);
            ca?.Dispose();
            return 1;
        }

        // -------- sidecar API host for the workbench "Intercepted" rail --------
        var apiBuilder = WebApplication.CreateSlimBuilder();
        apiBuilder.Logging.ClearProviders();
        apiBuilder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, options.ApiPort, l => l.Protocols = HttpProtocols.Http1);
        });
        apiBuilder.Services.AddSingleton(store);
        await using var api = apiBuilder.Build();
        api.MapBowireInterceptorEndpoints("");

        try
        {
            await api.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or InvalidOperationException)
        {
            await io.Err.WriteLineAsync($"bowire interceptor: could not bind sidecar-API port {options.ApiPort}: {ex.Message}").ConfigureAwait(false);
            await edge.StopAsync(CancellationToken.None).ConfigureAwait(false);
            ca?.Dispose();
            return 1;
        }

        var scheme = edgeCert is null ? "http" : "https";
        var bindHost = listenAddress.Equals(IPAddress.Any) ? "0.0.0.0" : listenAddress.ToString();
        await io.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"bowire interceptor: edge listening on {scheme}://{bindHost}:{edge.EdgePort} -> {upstreamUri}")).ConfigureAwait(false);
        await io.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"bowire interceptor: sidecar API on http://127.0.0.1:{options.ApiPort}/api/intercepted/flows  (capacity {options.Capacity})")).ConfigureAwait(false);
        if (ca is not null)
        {
            await io.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"bowire interceptor: HTTPS edge ENABLED (CA at {ca.CaCertPath}; install into trust store to avoid client warnings).")).ConfigureAwait(false);
        }
        if (options.AllowSelfSignedUpstream)
        {
            await io.Out.WriteLineAsync("bowire interceptor: upstream TLS validation DISABLED (--allow-self-signed-upstream).").ConfigureAwait(false);
        }
        await io.Out.WriteLineAsync("bowire interceptor: press Ctrl-C to stop.").ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* graceful */ }

        await api.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await edge.StopAsync(CancellationToken.None).ConfigureAwait(false);
        ca?.Dispose();
        return 0;
    }

    /// <summary>
    /// Parse a <c>host:port</c> string (e.g. <c>127.0.0.1:8080</c>,
    /// <c>0.0.0.0:9000</c>, or just <c>:8080</c>) into a bind tuple.
    /// </summary>
    internal static bool TryParseListen(string raw, out IPAddress address, out int port, out string error)
    {
        address = IPAddress.Loopback;
        port = 0;
        error = "";
        if (string.IsNullOrWhiteSpace(raw)) { error = "value is empty"; return false; }
        var idx = raw.LastIndexOf(':');
        if (idx < 0) { error = "expected host:port (got just '" + raw + "')"; return false; }
        var hostPart = raw[..idx];
        var portPart = raw[(idx + 1)..];
        if (!int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
            || port < 0 || port > 65535)
        {
            error = $"port '{portPart}' is not in 0..65535";
            return false;
        }
        if (string.IsNullOrEmpty(hostPart))
        {
            address = IPAddress.Loopback;
            return true;
        }
        if (!IPAddress.TryParse(hostPart, out var parsed))
        {
            error = $"host '{hostPart}' is not a valid IP literal (use 0.0.0.0 for any interface)";
            return false;
        }
        address = parsed;
        return true;
    }
}
