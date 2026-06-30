// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace Kuestenlogik.Bowire.Interceptor;

/// <summary>
/// Boots a standalone reverse-proxy host that fronts an upstream service
/// (#307 — Phase C of the #153 interceptor track; YARP migration in
/// #323). Two Kestrel hosts come up side-by-side:
/// <list type="bullet">
///   <item>the <em>edge</em> listener clients point at — every request
///   forwarded upstream by <see cref="BowireReverseProxyMiddleware"/>
///   using YARP's <see cref="IHttpForwarder"/>.<see cref="IHttpForwarder.SendAsync(Microsoft.AspNetCore.Http.HttpContext, string, HttpMessageInvoker, ForwarderRequestConfig, HttpTransformer)"/>,
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
/// disposes the forwarding <see cref="HttpMessageInvoker"/>.
/// </remarks>
/// <remarks>
/// <para>
/// #323 — YARP migration. The forwarder is now driven by YARP's
/// <see cref="IHttpForwarder"/>. We construct one <see cref="HttpMessageInvoker"/>
/// per host (per YARP's recommendation — pooled connections to the same
/// upstream are shared across requests) over a <see cref="SocketsHttpHandler"/>
/// with the same posture the old <c>HttpClientHandler</c> exposed
/// (no auto-redirect, no cookie jar, no automatic decompression so the
/// upstream's Content-Encoding flows through verbatim, dev-mode
/// self-signed override gated by <see cref="BowireReverseProxyHostOptions.AllowSelfSignedUpstream"/>).
/// The middleware itself owns the body-capture seam via a custom
/// <see cref="HttpTransformer"/> — see <see cref="BowireReverseProxyMiddleware"/>.
/// </para>
/// </remarks>
public sealed class BowireReverseProxyHost : IAsyncDisposable
{
    private readonly BowireReverseProxyHostOptions _options;
    private readonly InterceptedFlowStore _store;
    private readonly HttpMessageInvoker _invoker;
    private readonly SocketsHttpHandler _handler;
    private WebApplication? _edge;
    private bool _disposed;

    private BowireReverseProxyHost(
        BowireReverseProxyHostOptions options,
        InterceptedFlowStore store,
        HttpMessageInvoker invoker,
        SocketsHttpHandler handler)
    {
        _options = options;
        _store = store;
        _invoker = invoker;
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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5359:Do not disable certificate validation",
        Justification = "--allow-self-signed-upstream is an explicit opt-in for dev-mode upstreams (matches the bowire fuzz --allow-self-signed-certs flag).")]
    public static BowireReverseProxyHost Create(BowireReverseProxyHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Upstream is null) throw new ArgumentException("Upstream URL is required.", nameof(options));

        // SocketsHttpHandler is the YARP-recommended outbound socket — the
        // old HttpClientHandler ultimately wraps the same thing on .NET
        // Core, but SocketsHttpHandler exposes the knobs (Connect timeout,
        // multi-HTTP/2-connections, no automatic decompression so the
        // upstream's Content-Encoding flows through verbatim) without the
        // legacy WinHTTP indirection.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // Per-request timeout is enforced by ForwarderRequestConfig.ActivityTimeout
            // (the YARP-managed equivalent of HttpClient.Timeout) — see
            // BowireReverseProxyMiddleware. We deliberately do NOT clamp
            // SocketsHttpHandler.ResponseDrainTimeout etc. here; the
            // defaults match the YARP direct-forwarding sample.
        };
        if (options.AllowSelfSignedUpstream)
        {
            handler.SslOptions.RemoteCertificateValidationCallback =
                static (_, _, _, _) => true;
        }

        // HttpMessageInvoker (not HttpClient) is the YARP-recommended type:
        // HttpClient buffers responses by default which breaks streaming,
        // HttpMessageInvoker doesn't. We own disposal of the handler; the
        // invoker forwards Dispose to it.
        var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        var store = options.Store ?? new InterceptedFlowStore(options.Capacity);
        return new BowireReverseProxyHost(options, store, invoker, handler);
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
        // YARP IHttpForwarder + supporting services. AddHttpForwarder is
        // idempotent — calling it on a host that has its own AddReverseProxy
        // is fine.
        edgeBuilder.Services.AddHttpForwarder();

        var edge = edgeBuilder.Build();
        var forwarder = edge.Services.GetRequiredService<IHttpForwarder>();
        var requestConfig = new ForwarderRequestConfig
        {
            ActivityTimeout = _options.UpstreamTimeout,
            // Default to HTTP/1.1 with RequestVersionOrHigher: HTTPS
            // upstreams can negotiate HTTP/2 via ALPN, cleartext
            // upstreams stay on HTTP/1.1. YARP's own default is
            // HTTP/2 + RequestVersionOrLower which silently downgrades
            // for ALPN-less http:// upstreams but trips up servers
            // that misread the h2 attempt — the legacy HttpClient path
            // implicitly used HTTP/1.1 for cleartext, so this matches.
            Version = System.Net.HttpVersion.Version11,
            VersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrHigher,
        };

        // Mount the reverse-proxy middleware as the terminal handler — every
        // request, regardless of method/path, falls through to it.
        edge.Run(async ctx =>
        {
            var logger = ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<BowireReverseProxyMiddleware>();
            var mw = new BowireReverseProxyMiddleware(
                _store,
                forwarder,
                _invoker,
                requestConfig,
                _options.Upstream!,
                new BowireInterceptorOptions { MaxBodyBytes = _options.MaxBodyBytes },
                logger);
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
        _invoker.Dispose();
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
