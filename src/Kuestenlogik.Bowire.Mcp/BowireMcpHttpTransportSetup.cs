// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// <see cref="IConfigureOptions{T}"/> setup that latches the
/// originating request path on
/// <see cref="BowireMcpDualHandlerDispatcher"/> at session-init time
/// so the dispatcher can route <c>tools/list</c>, <c>tools/call</c>,
/// &amp;c. to the right handler based on the URL the JSON-RPC POST
/// hit — even after the request scope has unwound.
/// </summary>
/// <remarks>
/// <para>
/// Wraps any caller-supplied <see cref="HttpServerTransportOptions.ConfigureSessionOptions"/>
/// so the embedded host's own callbacks still fire. Idempotent on
/// repeat composition: the wrap only happens once per options
/// snapshot because <c>IConfigureOptions</c> instances are de-duped
/// by the options pipeline.
/// </para>
/// </remarks>
internal sealed class BowireMcpHttpTransportSetup
    : IConfigureOptions<HttpServerTransportOptions>
{
    private readonly BowireMcpDualHandlerDispatcher _dispatcher;

    public BowireMcpHttpTransportSetup(BowireMcpDualHandlerDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    public void Configure(HttpServerTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The streamable-HTTP transport calls ConfigureSessionOptions
        // once per session (or per request in stateless mode) with the
        // originating HttpContext in scope. The McpServerOptions
        // doesn't carry a stash slot for free-form data, but the
        // HttpContext does — Items[] is request-scoped in stateless
        // mode and propagates to RequestServices, so the dispatcher
        // can read it back via IHttpContextAccessor at tool-invoke
        // time. We mirror the path onto a sentinel options field too
        // (KnownClientInfo.Title is unused by the SDK's own logic
        // when no client info is sent, but stashing the path there
        // would still be abusive) — sticking to HttpContext.Items as
        // the single source of truth keeps the data flow clean.
        var previous = options.ConfigureSessionOptions;
        options.ConfigureSessionOptions = async (httpContext, mcpOptions, ct) =>
        {
            if (previous is not null)
                await previous(httpContext, mcpOptions, ct).ConfigureAwait(false);

            // Idempotent — the SDK only fires once per session, but
            // a host that re-installs this setup wouldn't end up
            // stashing a stale path either (the HttpContext.Items
            // dictionary is per-request, so each session writes its
            // own path on its own request).
            httpContext.Items[BowireMcpDualHandlerDispatcher.SessionRoutePathItemKey]
                = httpContext.Request.Path.Value ?? string.Empty;
        };
    }
}
