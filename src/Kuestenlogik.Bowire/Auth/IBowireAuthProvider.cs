// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
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

    /// <summary>
    /// Optional hook for providers that need to insert middleware into
    /// the host pipeline — e.g. an OIDC callback path that lives on a
    /// stable URL outside the Bowire endpoint group, a claims-
    /// transformation step, or a redirect-on-401 handler. Called from
    /// <c>BowireApiEndpoints.Map</c> after the standard
    /// <c>UseAuthentication</c> / <c>UseAuthorization</c> calls but
    /// before the Bowire endpoint group is materialised, so anything
    /// the provider mounts here runs before Bowire's own routes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default implementation is a no-op — most providers configure
    /// everything they need through <see cref="AddAuthentication"/>
    /// because ASP.NET's authentication middleware handles the
    /// challenge / sign-in / sign-out handshake without any extra
    /// host wiring. Only override when the provider's scheme can't
    /// be expressed as pure DI registration.
    /// </para>
    /// <para>
    /// <b>Privilege boundary.</b> Auth providers loaded as sibling
    /// plugins (under <c>~/.bowire/plugins/</c>) still load through
    /// the standard <see cref="PluginLoading.BowirePluginLoadContext"/>;
    /// they share the host's copy of every <c>Microsoft.*</c> assembly,
    /// so the <see cref="IApplicationBuilder"/> they receive here is
    /// the real host pipeline -- not a sandbox. Treat this hook like
    /// any other middleware-registration point: anything mounted here
    /// runs with full host trust. Embedded hosts that want a tighter
    /// boundary can wrap the auth plugin in a separate load context
    /// before passing it to <c>AddBowireAuth</c>.
    /// </para>
    /// </remarks>
    void Configure(IApplicationBuilder app)
    {
        // No-op default. OIDC, SAML, and API-key providers can all
        // operate purely through AddAuthentication; only providers
        // that need custom middleware override this.
    }
}
