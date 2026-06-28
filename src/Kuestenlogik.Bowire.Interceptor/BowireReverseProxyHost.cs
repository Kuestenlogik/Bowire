// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Interceptor;

/// <summary>
/// Boots a standalone reverse-proxy host that fronts an upstream service
/// (#307 — Phase C of the #153 interceptor track). Two Kestrel hosts come
/// up side-by-side:
/// <list type="bullet">
///   <item>the <em>edge</em> listener clients point at — every request
///   forwarded upstream by <see cref="BowireReverseProxyMiddleware"/>,
///   captured into <see cref="InterceptedFlowStore"/>;</item>
///   <item>the <em>sidecar</em> API the workbench's "Intercepted" rail
///   reads (<c>/api/intercepted/flows</c> / <c>/stream</c>), backed by
///   the same store singleton.</item>
/// </list>
/// </summary>
/// <remarks>
/// The split-host shape mirrors <c>bowire proxy</c>'s proxy + sidecar
/// API design exactly, so an operator who learned one transfers
/// straight to the other. Stop semantics are graceful — cancelling the
/// supplied <see cref="CancellationToken"/> stops both hosts and
/// disposes the forwarding <see cref="HttpClient"/>.
/// </remarks>
public sealed class BowireReverseProxyHost : IAsyncDisposable
{
    private readonly BowireReverseProxyHostOptions _options;
    private readonly InterceptedFlowStore _store;
    private readonly HttpClient _client;
    private readonly HttpClientHandler _handler;
    private WebApplication? _edge;
    private bool _disposed;

    private BowireReverseProxyHost(
        BowireReverseProxyHostOptions options,
        InterceptedFlowStore store,
        HttpClient client,
        HttpClientHandler handler)
    {
        _options = options;
        _store = store;
        _client = client;
        _handler = handler;
    }

    /// <summary>The actual TCP port the edge listener bound to (0 -> ephemeral).</summary>
    public int EdgePort { get; private set; }

    /// <summary>The flow store the edge middleware writes captures into.</summary>
    public InterceptedFlowStore Store => _store;

    /// <summary>
    /// Build a configured reverse-proxy host. Does NOT start the listener
    /// — call <see cref="StartAsync"/>. The split lets the CLI catch
    /// bind failures separately from forwarder-setup failures.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5400:HttpClient may be created without enabling CheckCertificateRevocationList",
        Justification = "CRL check is enabled by default; --allow-self-signed-upstream is an explicit opt-in for dev-mode upstreams (matches the bowire fuzz --allow-self-signed-certs flag).")]
    public static BowireReverseProxyHost Create(BowireReverseProxyHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Upstream is null) throw new ArgumentException("Upstream URL is required.", nameof(options));

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            CheckCertificateRevocationList = !options.AllowSelfSignedUpstream,
        };
        if (options.AllowSelfSignedUpstream)
        {
            handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
        }
        var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = options.UpstreamTimeout,
        };
        var store = options.Store ?? new InterceptedFlowStore(options.Capacity);
        return new BowireReverseProxyHost(options, store, client, handler);
    }

    /// <summary>Start the edge listener. After this returns, <see cref="EdgePort"/> is bound.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var edgeBuilder = WebApplication.CreateSlimBuilder();
        edgeBuilder.Logging.ClearProviders();
        edgeBuilder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(_options.ListenAddress, _options.ListenPort, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                if (_options.ServerCertificate is not null)
                {
                    listen.UseHttps(_options.ServerCertificate);
                }
            });
        });
        edgeBuilder.Services.AddSingleton(_store);
        edgeBuilder.Services.AddSingleton(new BowireInterceptorOptions
        {
            MaxBodyBytes = _options.MaxBodyBytes,
            Enabled = true,
        });

        var edge = edgeBuilder.Build();
        // Mount the reverse-proxy middleware as the terminal handler — every
        // request, regardless of method/path, falls through to it.
        edge.Run(async ctx =>
        {
            var logger = ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<BowireReverseProxyMiddleware>();
            var mw = new BowireReverseProxyMiddleware(_store, _client, _options.Upstream!,
                new BowireInterceptorOptions { MaxBodyBytes = _options.MaxBodyBytes }, logger);
            await mw.InvokeAsync(ctx).ConfigureAwait(false);
        });

        await edge.StartAsync(cancellationToken).ConfigureAwait(false);

        // Resolve the bound port — server.Features.Get<IServerAddressesFeature>()
        // is the canonical post-Start lookup.
        EdgePort = ExtractPort(edge) ?? _options.ListenPort;
        _edge = edge;
    }

    /// <summary>Stop the edge listener (idempotent). Sidecar API stops via its own caller.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_edge is not null)
        {
            await _edge.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_edge is not null)
        {
            try { await _edge.DisposeAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* race with parallel stop */ }
        }
        _client.Dispose();
        _handler.Dispose();
    }

    private static int? ExtractPort(WebApplication app)
    {
        var addresses = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            ?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
            ?.Addresses;
        if (addresses is null) return null;
        foreach (var addr in addresses)
        {
            if (Uri.TryCreate(addr, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
        }
        return null;
    }
}

/// <summary>
/// Construction-time configuration for <see cref="BowireReverseProxyHost"/>.
/// </summary>
public sealed class BowireReverseProxyHostOptions
{
    /// <summary>Upstream service the edge listener forwards to. Required.</summary>
    public Uri? Upstream { get; init; }

    /// <summary>Edge listen address. Default <see cref="IPAddress.Loopback"/>.</summary>
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;

    /// <summary>Edge listen port. 0 lets the OS pick (test-friendly).</summary>
    public int ListenPort { get; init; }

    /// <summary>Per-side body capture cap. Default 1 MiB.</summary>
    public int MaxBodyBytes { get; init; } = 1024 * 1024;

    /// <summary>Flow-store ring capacity. Default 1000.</summary>
    public int Capacity { get; init; } = 1000;

    /// <summary>Optional pre-built store to share (e.g. with a workbench host).</summary>
    public InterceptedFlowStore? Store { get; init; }

    /// <summary>Upstream HTTP timeout. Default 100 s (HttpClient default).</summary>
    public TimeSpan UpstreamTimeout { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Accept the upstream's TLS cert without chain validation. Useful when
    /// fronting a dev-mode service with a self-signed cert; off by default.
    /// </summary>
    public bool AllowSelfSignedUpstream { get; init; }

    /// <summary>
    /// Optional TLS certificate to present on the edge. When set, the edge
    /// listener serves HTTPS — clients trust whatever CA signed this cert
    /// (typically Bowire's MITM CA, reusing #36's flow).
    /// </summary>
    public X509Certificate2? ServerCertificate { get; init; }
}
