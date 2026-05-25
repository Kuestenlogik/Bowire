// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Web;

namespace Kuestenlogik.Bowire.Auth.Oidc;

/// <summary>
/// OpenID Connect <see cref="IBowireAuthProvider"/> built on
/// Microsoft.Identity.Web. Activated by
/// <c>--auth-provider oidc</c> (or
/// <c>Bowire:Auth:ProviderId=oidc</c>); reads its own settings under
/// <c>Bowire:Auth:Oidc:*</c>:
///
/// <code>
/// {
///   "Bowire": {
///     "Auth": {
///       "ProviderId": "oidc",
///       "Oidc": {
///         "Authority": "https://login.example.com/",
///         "ClientId": "&lt;app-client-id&gt;",
///         "Audience": "&lt;optional audience override&gt;",
///         "RequiredClaim": { "groups": "bowire-users" }
///       }
///     }
///   }
/// }
/// </code>
///
/// <para>
/// Wire-up: the provider registers JWT-bearer authentication via
/// <c>Microsoft.Identity.Web.AddMicrosoftIdentityWebApi</c>, which is
/// the OIDC-compliant path for protecting an API (as opposed to a
/// browser app). The workbench's HTML shell stays anonymous; the
/// <c>/api/*</c> endpoints under <see cref="BowireApiEndpoints"/> are
/// gated by the <see cref="BowireAuthPolicies.Default"/> policy.
/// </para>
///
/// <para>
/// Phase-A scope: "lock the door" — every authenticated caller still
/// sees the same <c>~/.bowire/</c>. Per-user data separation is Phase
/// B and depends on the <c>IBowireUserStore</c> seam landing first.
/// </para>
/// </summary>
public sealed class OidcAuthProvider : IBowireAuthProvider
{
    public string Id => "oidc";
    public string Name => "OpenID Connect";

    public void AddAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Microsoft.Identity.Web reads its own AzureAd / Authority /
        // ClientId / etc. keys off a configuration section. Re-export
        // the Bowire:Auth:Oidc subtree under that name so operators
        // only have to learn one set of keys.
        var oidcSection = configuration.GetSection("Bowire:Auth:Oidc");

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(oidcSection);
    }

    public void BuildDefaultPolicy(AuthorizationPolicyBuilder policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        // Minimum bar: authenticated. The required-claim filter below
        // is layered on at the host level via
        // services.Configure<AuthorizationOptions> if the operator set
        // Bowire:Auth:Oidc:RequiredClaim — keeping it out of this
        // method means the policy stays purely about "authenticated"
        // and host policy keeps the gating knob.
        policy.RequireAuthenticatedUser();
    }
}
