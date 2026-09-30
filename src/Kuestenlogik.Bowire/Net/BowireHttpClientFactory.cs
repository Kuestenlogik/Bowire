// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Security;
using Kuestenlogik.Bowire.Auth;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Net;

/// <summary>
/// Builds an <see cref="HttpClient"/> whose certificate validation callback
/// consults <see cref="LocalhostCertTrust"/> on every request. Lets HttpClient-
/// based protocol plugins (REST, GraphQL, SSE, MCP, OData) opt into the same
/// loopback-cert relaxation that SignalR / WebSocket already use, without each
/// plugin re-implementing the validation-callback dance.
/// <para>
/// Defence in depth — the relaxed callback only returns <c>true</c> when both
/// (a) the OS trust check failed *and* (b) <see cref="LocalhostCertTrust.IsTrustedFor"/>
/// returns true for the request URL. A misconfigured production host where
/// `Bowire:TrustLocalhostCert=true` was set against a non-loopback URL still
/// validates strictly.
/// </para>
/// </summary>
public static class BowireHttpClientFactory
{
    /// <summary>
    /// Creates a long-lived <see cref="HttpClient"/> suitable for a plugin's
    /// instance field. The validation callback closes over <paramref name="config"/>
    /// and <paramref name="pluginId"/> so per-plugin overrides
    /// (<c>Bowire:{pluginId}:TrustLocalhostCert</c>) keep working.
    /// </summary>
    /// <param name="config">
    /// Application <see cref="IConfiguration"/>; usually obtained via
    /// <c>serviceProvider.GetService&lt;IConfiguration&gt;()</c> in
    /// <c>IBowireProtocol.Initialize</c>. Pass <c>null</c> to disable the
    /// relaxed callback entirely (standalone test paths).
    /// </param>
    /// <param name="pluginId">The plugin id (e.g. <c>"rest"</c>, <c>"graphql"</c>).</param>
    /// <param name="timeout">Optional client timeout. Default: <c>HttpClient</c> default (100 s).</param>
    public static HttpClient Create(IConfiguration? config, string pluginId, TimeSpan? timeout = null)
    {
        // CA2000: handler ownership transfers to HttpClient via
        // disposeHandler:true. HttpClient's ctor never throws for a non-
        // null handler, and the only follow-up statement (`client.Timeout =
        // ...`) executes after ownership has moved into the client — so
        // no leak path exists. A try/catch null-out dance just trades the
        // warning for CA1508 ("variable is always null") because the
        // analyzer also can't model that ownership transfer. Pragma is
        // the honest answer.
#pragma warning disable CA2000
        var handler = CreateHandler(config, pluginId);
        // #679 — a request carrying Negotiate / NTLM / Digest credentials goes
        // through a handler of its own per credential set.
        var routed = new BowireHttpAuthRoutingHandler(handler, () => CreateHandler(config, pluginId));
#pragma warning restore CA2000
        var client = new HttpClient(routed, disposeHandler: true);
        if (timeout.HasValue) client.Timeout = timeout.Value;
        return client;
    }

    /// <summary>
    /// A client that authenticates every request with <paramref name="auth"/>
    /// (#679) — for a caller whose requests are built by a library, so the
    /// credentials cannot ride on each request. With <c>null</c> it is a plain <c>Create</c>.
    /// </summary>
    public static HttpClient CreateAuthenticated(IConfiguration? config, string pluginId, TimeSpan? timeout, Auth.BowireHttpAuthConfig? auth)
    {
        if (auth is null) return Create(config, pluginId, timeout);
#pragma warning disable CA2000 // Ownership moves into the HttpClient (disposeHandler: true).
        var handler = CreateHandler(config, pluginId);
        Auth.BowireHttpAuth.ApplyTo(handler, auth);
        var client = new HttpClient(Auth.BowireHttpAuth.Wrap(handler, auth), disposeHandler: true);
#pragma warning restore CA2000
        if (timeout.HasValue) client.Timeout = timeout.Value;
        return client;
    }

    /// <summary>
    /// Same as <see cref="Create"/> but exposes the underlying <see cref="HttpClientHandler"/>
    /// — useful for plugins that need to layer additional configuration on
    /// top (cookies, proxies, redirect policy) before wrapping it in an
    /// <see cref="HttpClient"/>.
    /// </summary>
    public static HttpClientHandler CreateHandler(IConfiguration? config, string pluginId)
    {
        // #681 — no cookies of its own: a shared plugin client used to keep
        // whatever any server set and send it to that server on every later
        // call, whichever environment it came from. Cookies now come only
        // from the environment's jar (BowireHttpAuthRoutingHandler).
        var handler = new HttpClientHandler { UseCookies = false };

#pragma warning disable CA5359 // The relaxed path is double-guarded inside the callback.
        handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
        {
            // Strict path: the OS trust store accepted the cert. No relaxation needed.
            if (errors == SslPolicyErrors.None) return true;

            // Relaxed path: only honour for loopback URLs *and* when the host
            // explicitly opted in via Bowire:TrustLocalhostCert (or the
            // per-plugin override). Both checks live inside
            // LocalhostCertTrust.IsTrustedFor so the same logic that
            // SignalR / WebSocket use applies here.
            var url = request.RequestUri?.ToString() ?? string.Empty;
            return LocalhostCertTrust.IsTrustedFor(config, pluginId, url);
        };
#pragma warning restore CA5359

        // #680 — the proxy, the bypass list and the CA bundle.
        return BowireNetworkPolicy.Apply(handler);
    }

    /// <summary>
    /// Builds a <see cref="SocketsHttpHandler"/> with the same loopback-cert
    /// opt-in as <see cref="CreateHandler"/>, but configured for protocols
    /// that need HTTP/2 directly (gRPC's <see cref="System.Net.Http.HttpClient"/>-
    /// less channel). The validation callback consults
    /// <see cref="LocalhostCertTrust"/> per request, so per-plugin overrides
    /// keep working the same way as on the regular HttpClient path.
    /// </summary>
    /// <param name="config">Application <see cref="IConfiguration"/>; null disables relaxed validation.</param>
    /// <param name="pluginId">Plugin id used for <c>Bowire:{pluginId}:TrustLocalhostCert</c> overrides.</param>
    /// <param name="serverUrl">
    /// Optional target URL used to gate the validation callback. The plugin
    /// gives us the URL eagerly (rather than reading <c>request.RequestUri</c>
    /// inside the callback) because gRPC's <see cref="System.Net.Security.RemoteCertificateValidationCallback"/>
    /// is the SslStream-level one — it doesn't carry an HttpRequestMessage,
    /// so we close over the URL here instead.
    /// </param>
    public static SocketsHttpHandler CreateSocketsHttpHandler(
        IConfiguration? config, string pluginId, string? serverUrl = null)
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };

        // Wire the same trust opt-in as the HttpClient path. SocketsHttpHandler
        // exposes SslOptions.RemoteCertificateValidationCallback (SslStream
        // level) rather than the HttpClientHandler's per-request callback.
        // We close over the caller-supplied serverUrl so the loopback gate
        // still applies — the SslStream callback doesn't see a request URI.
        handler.SslOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
        {
#pragma warning disable CA5359
            if (errors == System.Net.Security.SslPolicyErrors.None) return true;
            return !string.IsNullOrEmpty(serverUrl)
                && LocalhostCertTrust.IsTrustedFor(config, pluginId, serverUrl);
#pragma warning restore CA5359
        };

        // #680 — the proxy, the bypass list and the CA bundle.
        return BowireNetworkPolicy.Apply(handler);
    }
}
