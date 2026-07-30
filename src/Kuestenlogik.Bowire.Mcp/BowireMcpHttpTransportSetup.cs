// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// <see cref="IConfigureOptions{T}"/> setup that stashes the originating
/// request path where <see cref="BowireMcpDualHandlerDispatcher"/> can
/// read it, so the dispatcher can route <c>tools/list</c>,
/// <c>tools/call</c>, &amp;c. to the right handler based on the URL the
/// JSON-RPC POST hit.
/// </summary>
/// <remarks>
/// <para>
/// Wraps any caller-supplied <see cref="HttpServerTransportOptions.ConfigureSessionOptions"/>
/// so the embedded host's own callbacks still fire. Idempotent on
/// repeat composition: the wrap only happens once per options
/// snapshot because <c>IConfigureOptions</c> instances are de-duped
/// by the options pipeline.
/// </para>
/// <para>
/// "Session" is now a historical name for the hook. MCP revision
/// 2026-07-28 dropped HTTP sessions, and every Bowire mount pins
/// <c>Stateless = true</c>, so the SDK invokes this callback once per
/// request with that request's <c>HttpContext</c> — not once per session
/// at init time. That is strictly better for this seam: the stash and
/// the live-<c>HttpContext</c> fallback the dispatcher uses now read the
/// same request instead of diverging after the first one.
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
        // with the originating HttpContext in scope — once per request on
        // a stateless mount, which is every Bowire mount and the SDK
        // default since 2.0.0. The McpServerOptions
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

            // Idempotent, and stale-proof by construction: the callback
            // fires per request on a stateless mount, and
            // HttpContext.Items is per-request storage, so each request
            // writes its own path into its own dictionary. A host that
            // re-installs this setup just writes the same value twice.
            httpContext.Items[BowireMcpDualHandlerDispatcher.SessionRoutePathItemKey]
                = httpContext.Request.Path.Value ?? string.Empty;
        };
    }
}
