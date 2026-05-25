// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// Extension point for "who is allowed to use this Bowire workbench" —
/// sibling to <c>IBowireProtocol</c> (wire plugins) and
/// <c>IBowireUiExtension</c> (UI widgets). Concrete providers ship as
/// separate NuGet packages (e.g. <c>Kuestenlogik.Bowire.Auth.Oidc</c>)
/// so heavyweight dependencies (Microsoft.Identity.Web, SAML libs, &amp;c)
/// only land in installs that actually use them.
/// </summary>
/// <remarks>
/// <para>
/// Discovery follows the same assembly-scan pattern as
/// <see cref="BowireProtocolRegistry"/>: providers are picked up at
/// startup if their assembly is loaded (PackageReference for embedded
/// hosts, <c>~/.bowire/plugins/</c> for standalone). At most one
/// provider is active per process — selected by the
/// <c>--auth-provider &lt;id&gt;</c> CLI flag, the
/// <c>Bowire:AuthProvider</c> appsettings key, or the
/// <see cref="BowireAuthOptions.ProviderId"/> property when calling
/// <c>AddBowireAuth()</c> directly.
/// </para>
/// <para>
/// When no provider is selected (the default), Bowire's endpoints
/// stay open — same behaviour as today's laptop-friendly default.
/// When a provider is selected,
/// <see cref="BowireAuthProviderRegistry.ApplyAuthentication"/> wires
/// up the auth scheme + an <see cref="AuthorizationPolicy"/> that
/// every Bowire endpoint enforces via
/// <c>.RequireAuthorization(BowireAuthPolicies.Default)</c>.
/// </para>
/// <para>
/// Embedded mode is unchanged: when the host has its own auth
/// pipeline configured, the host's policy wins. Bowire only attaches
/// its own scheme when the host *opts in* via <c>--auth-provider</c>
/// or the appsettings equivalent.
/// </para>
/// </remarks>
public interface IBowireAuthProvider
{
    /// <summary>
    /// Stable id used to select this provider on the CLI / in config
    /// (e.g. <c>"oidc"</c>, <c>"saml"</c>, <c>"apikey"</c>). Compared
    /// case-insensitively against <c>--auth-provider &lt;id&gt;</c>.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Human-readable provider name shown in the workbench's
    /// "signed in via" surface and in startup logs. Examples:
    /// <c>"OpenID Connect"</c>, <c>"SAML 2.0"</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Register the authentication scheme + any provider-specific
    /// services. Called once at startup, before
    /// <c>builder.Build()</c>. Implementations should read their
    /// configuration off <paramref name="configuration"/> under
    /// <c>Bowire:Auth:&lt;Id&gt;</c> (e.g.
    /// <c>Bowire:Auth:Oidc:Authority</c>).
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="configuration">
    /// Configuration root — providers should look at
    /// <c>Bowire:Auth:&lt;Id&gt;</c> for their own keys.
    /// </param>
    void AddAuthentication(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// Configure the policy that <c>BowireApiEndpoints</c> enforces
    /// on every Bowire route. The default implementation requires
    /// authenticated user; providers can tighten it (e.g. require a
    /// specific claim, role, or audience).
    /// </summary>
    /// <param name="policy">
    /// Builder pre-seeded with
    /// <see cref="AuthorizationPolicyBuilder.RequireAuthenticatedUser"/>.
    /// Override or extend in concrete providers.
    /// </param>
    void BuildDefaultPolicy(AuthorizationPolicyBuilder policy)
    {
        // Default: require authenticated. Provider can replace via
        // policy.Requirements.Clear() + own requirements.
        policy.RequireAuthenticatedUser();
    }
}
