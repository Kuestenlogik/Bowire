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
///         "RequiredClaim": {
///           "groups": "bowire-users",
///           "tenant_id": "acme"
///         }
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

    /// <summary>
    /// Parsed <c>Bowire:Auth:Oidc:RequiredClaim</c> dictionary — every
    /// entry becomes a <c>policy.RequireClaim(claimType, claimValue)</c>
    /// on the default policy. <c>null</c> when the section is absent or
    /// empty (no extra gating beyond the authenticated-user check).
    /// Populated by <see cref="AddAuthentication"/> and read by
    /// <see cref="BuildDefaultPolicy"/>; both run at startup against
    /// the same provider instance (registered as a singleton via
    /// <c>BowireAuthProviderRegistry.ApplyAuthentication</c>), so the
    /// field doesn't need synchronisation.
    /// </summary>
    private IReadOnlyDictionary<string, string>? _requiredClaims;

    public void AddAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Microsoft.Identity.Web reads its own AzureAd / Authority /
        // ClientId / etc. keys off a configuration section. Re-export
        // the Bowire:Auth:Oidc subtree under that name so operators
        // only have to learn one set of keys.
        var oidcSection = configuration.GetSection("Bowire:Auth:Oidc");

        // Pull RequiredClaim entries up-front so BuildDefaultPolicy
        // can apply them without re-reading the config. Empty section
        // -> _requiredClaims stays null and BuildDefaultPolicy skips
        // the layering step.
        var claimChildren = oidcSection.GetSection("RequiredClaim").GetChildren().ToList();
        if (claimChildren.Count > 0)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in claimChildren)
            {
                if (!string.IsNullOrEmpty(child.Key) && !string.IsNullOrEmpty(child.Value))
                {
                    dict[child.Key] = child.Value;
                }
            }
            if (dict.Count > 0)
            {
                _requiredClaims = dict;
            }
        }

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(oidcSection);

        // Stash the access token on the authentication ticket so the
        // workbench's `/api/auth/session` endpoint can hand it back to
        // the user's request-pane Auth tab ("Use my session token"
        // forwarding mode). SaveToken=false is the JwtBearer default
        // for size reasons -- our use case keeps the token in process
        // memory only, off the wire, so the size hit is acceptable in
        // exchange for the token-forwarding feature.
        services.Configure<JwtBearerOptions>(
            JwtBearerDefaults.AuthenticationScheme,
            o => o.SaveToken = true);
    }

    public void BuildDefaultPolicy(AuthorizationPolicyBuilder policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        // Minimum bar: authenticated.
        policy.RequireAuthenticatedUser();

        // Layer the operator-configured required claims on top. Each
        // entry adds a separate RequireClaim requirement -- the policy
        // requires ALL of them to match (AND semantics). For "user must
        // be in group X OR group Y" semantics, set a single key to a
        // value the upstream IdP issues for both groups, or compose a
        // more complex policy via a Phase-B custom IAuthorizationHandler.
        if (_requiredClaims is not null)
        {
            foreach (var (claimType, claimValue) in _requiredClaims)
            {
                policy.RequireClaim(claimType, claimValue);
            }
        }
    }
}
