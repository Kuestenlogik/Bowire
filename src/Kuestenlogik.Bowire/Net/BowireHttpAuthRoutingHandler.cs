// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net.Http;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Net;

/// <summary>
/// Sends a request that carries challenge-response credentials (#679) through
/// a handler of its own — one per credential set — and every other request
/// through the plugin's shared handler.
/// </summary>
/// <remarks>
/// NTLM and Negotiate authenticate the TCP connection, not the request. A
/// pooled connection authenticated as one user would carry the next user's
/// request as the first; a handler per credential set keeps their pools apart.
/// Built from the same factory as the shared handler, so proxy, CA bundle
/// and the loopback trust opt-in apply to authenticated calls as well.
/// </remarks>
internal sealed class BowireHttpAuthRoutingHandler : DelegatingHandler
{
    // A dev tool sees a handful of identities; past this, start over rather
    // than hold sockets for every identity ever tried.
    private const int MaxIdentities = 16;

    private readonly Func<HttpClientHandler> _createHandler;
    private readonly ConcurrentDictionary<string, HttpMessageInvoker> _byIdentity = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public BowireHttpAuthRoutingHandler(HttpMessageHandler shared, Func<HttpClientHandler> createHandler)
        : base(shared)
    {
        _createHandler = createHandler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Options.TryGetValue(BowireHttpAuth.OptionKey, out var auth))
            return base.SendAsync(request, cancellationToken);

        var invoker = _byIdentity.GetOrAdd(auth.CacheKey, _ => Build(auth));
        return invoker.SendAsync(request, cancellationToken);
    }

    private HttpMessageInvoker Build(BowireHttpAuthConfig auth)
    {
        lock (_gate)
        {
            if (_byIdentity.Count >= MaxIdentities) ClearIdentities();
        }
#pragma warning disable CA2000 // Owned by the invoker, which this handler disposes.
        var handler = _createHandler();
#pragma warning restore CA2000
        BowireHttpAuth.ApplyTo(handler, auth);
        return new HttpMessageInvoker(BowireHttpAuth.Wrap(handler, auth), disposeHandler: true);
    }

    private void ClearIdentities()
    {
        foreach (var invoker in _byIdentity.Values) invoker.Dispose();
        _byIdentity.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ClearIdentities();
        base.Dispose(disposing);
    }
}
