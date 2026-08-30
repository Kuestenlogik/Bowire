// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// Puts a mount behind the workbench's auth gate, if there is one (#625).
/// </summary>
/// <remarks>
/// <para>
/// <c>BowireApiEndpoints</c> applies the gate once to its own route group, and
/// everything inside inherits it. Anything mounted <em>outside</em> that group
/// — the MCP server, the MCP adapter — inherits nothing, and there is no
/// compiler error when it forgets. On an install with an identity provider
/// configured that meant the workbench and <c>/api/*</c> required a session
/// while <c>/mcp</c> did not, even though MCP drives the same operations.
/// </para>
/// <para>
/// Calling this unconditionally is the intended use: without a registered
/// provider it does nothing, which is the laptop default.
/// </para>
/// </remarks>
public static class BowireAuthGate
{
    /// <summary>
    /// Require the workbench's default policy when an auth provider is
    /// registered; do nothing otherwise.
    /// </summary>
    public static TBuilder RequireBowireAuth<TBuilder>(this TBuilder builder, IServiceProvider services)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(services);

        if (services.GetService<IBowireAuthProvider>() is not null)
        {
            builder.RequireAuthorization(BowireAuthPolicies.Default);
        }

        return builder;
    }
}
