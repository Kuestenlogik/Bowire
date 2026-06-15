// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth.Oidc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Auth.Oidc.Tests;

/// <summary>
/// Behaviour tests for <see cref="OidcAuthProvider"/>: the Id/Name
/// contract, the Bowire:Auth:Oidc config layering, JwtBearer
/// registration, the SaveToken pinning, and the RequiredClaim
/// AND-policy composition.
/// </summary>
public sealed class OidcAuthProviderTests
{
    [Fact]
    public void Id_IsOidc()
    {
        var sut = new OidcAuthProvider();
        Assert.Equal("oidc", sut.Id);
    }

    [Fact]
    public void Name_IsOpenIDConnect()
    {
        var sut = new OidcAuthProvider();
        Assert.Equal("OpenID Connect", sut.Name);
    }

    [Fact]
    public void AddAuthentication_NullServices_Throws()
    {
        var sut = new OidcAuthProvider();
        var cfg = new ConfigurationBuilder().Build();
        Assert.Throws<ArgumentNullException>(() => sut.AddAuthentication(null!, cfg));
    }

    [Fact]
    public void AddAuthentication_NullConfiguration_Throws()
    {
        var sut = new OidcAuthProvider();
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => sut.AddAuthentication(services, null!));
    }

    [Fact]
    public void BuildDefaultPolicy_NullPolicy_Throws()
    {
        var sut = new OidcAuthProvider();
        Assert.Throws<ArgumentNullException>(() => sut.BuildDefaultPolicy(null!));
    }

    [Fact]
    public async Task AddAuthentication_RegistersJwtBearerScheme()
    {
        // Microsoft.Identity.Web's AddMicrosoftIdentityWebApi wires the
        // JwtBearer scheme. We verify the scheme is registered via the
        // IAuthenticationSchemeProvider — its options graph has its
        // own DI requirements (IHostEnvironment etc.) so re-checking
        // the scheme itself is the contract we can pin without
        // pulling in the full ASP.NET hosting stack.
        var sut = new OidcAuthProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["Bowire:Auth:Oidc:Authority"] = "https://example.com/",
            ["Bowire:Auth:Oidc:ClientId"]  = "client-abc",
        });

        sut.AddAuthentication(services, cfg);

        using var sp = services.BuildServiceProvider();
        var schemeProvider = sp.GetRequiredService<IAuthenticationSchemeProvider>();
        var scheme = await schemeProvider.GetSchemeAsync(JwtBearerDefaults.AuthenticationScheme);
        Assert.NotNull(scheme);
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, scheme!.Name);
    }

    [Fact]
    public void AddAuthentication_RegistersJwtBearerOptionsConfigurator()
    {
        // Phase A "Use my session token" forwarding depends on
        // SaveToken=true. The provider achieves this via
        // services.Configure<JwtBearerOptions>(scheme, o => o.SaveToken = true);
        // which registers an IConfigureOptions<JwtBearerOptions>.
        // Running the resolved options graph end-to-end needs
        // Microsoft.Identity.Web's IHostEnvironment + other hosting
        // services that this isolated DI container doesn't have, so
        // we pin the registration shape (at least one named
        // configurator for the JwtBearer scheme) rather than the
        // resolved value. The end-to-end "SaveToken=true after the
        // full options pipeline" pins live with the integration test
        // harness that boots a real WebApplication.
        var sut = new OidcAuthProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        sut.AddAuthentication(services, BuildConfig(new()
        {
            ["Bowire:Auth:Oidc:Authority"] = "https://example.com/",
            ["Bowire:Auth:Oidc:ClientId"]  = "c",
        }));

        var configurators = services
            .Where(d => d.ServiceType == typeof(IConfigureOptions<JwtBearerOptions>))
            .ToList();
        Assert.NotEmpty(configurators);
    }

    [Fact]
    public void BuildDefaultPolicy_NoRequiredClaim_AuthenticatedOnly()
    {
        var sut = new OidcAuthProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        sut.AddAuthentication(services, BuildConfig(new()
        {
            ["Bowire:Auth:Oidc:Authority"] = "https://example.com/",
            ["Bowire:Auth:Oidc:ClientId"]  = "c",
        }));

        var pb = new AuthorizationPolicyBuilder();
        sut.BuildDefaultPolicy(pb);
        var policy = pb.Build();

        // RequireAuthenticatedUser surfaces as a
        // DenyAnonymousAuthorizationRequirement on the policy.
        Assert.Contains(policy.Requirements,
            r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
        // No claim requirements without RequiredClaim entries.
        Assert.DoesNotContain(policy.Requirements,
            r => r is Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement);
    }

    [Fact]
    public void BuildDefaultPolicy_RequiredClaim_AddsRequireClaimPerEntry()
    {
        var sut = new OidcAuthProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        sut.AddAuthentication(services, BuildConfig(new()
        {
            ["Bowire:Auth:Oidc:Authority"] = "https://example.com/",
            ["Bowire:Auth:Oidc:ClientId"]  = "c",
            ["Bowire:Auth:Oidc:RequiredClaim:groups"]    = "bowire-users",
            ["Bowire:Auth:Oidc:RequiredClaim:tenant_id"] = "acme",
        }));

        var pb = new AuthorizationPolicyBuilder();
        sut.BuildDefaultPolicy(pb);
        var policy = pb.Build();

        // Two ClaimsAuthorizationRequirement entries — one per
        // RequiredClaim key (AND semantics in the policy).
        var claims = policy.Requirements
            .OfType<Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement>()
            .ToList();
        Assert.Equal(2, claims.Count);
        Assert.Contains(claims, c => c.ClaimType == "groups"    && c.AllowedValues!.Contains("bowire-users"));
        Assert.Contains(claims, c => c.ClaimType == "tenant_id" && c.AllowedValues!.Contains("acme"));
    }

    [Fact]
    public void BuildDefaultPolicy_RequiredClaim_EmptyKeyOrValue_Skipped()
    {
        // Defensive: empty-string Key or Value entries shouldn't
        // produce a half-formed RequireClaim. Configuration providers
        // sometimes surface keys with no value (e.g. JSON `"x": null`),
        // and a half-formed RequireClaim would silently lock everyone
        // out at runtime.
        var sut = new OidcAuthProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        sut.AddAuthentication(services, BuildConfig(new()
        {
            ["Bowire:Auth:Oidc:Authority"] = "https://example.com/",
            ["Bowire:Auth:Oidc:ClientId"]  = "c",
            ["Bowire:Auth:Oidc:RequiredClaim:groups"] = "bowire-users",
            ["Bowire:Auth:Oidc:RequiredClaim:empty"]  = "",
        }));

        var pb = new AuthorizationPolicyBuilder();
        sut.BuildDefaultPolicy(pb);
        var policy = pb.Build();

        var claims = policy.Requirements
            .OfType<Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement>()
            .ToList();
        Assert.Single(claims);
        Assert.Equal("groups", claims[0].ClaimType);
    }

    [Fact]
    public void BuildDefaultPolicy_RequiredClaimMissing_ButOtherOidcSettingsPresent_StillAuthenticatedOnly()
    {
        // The Bowire:Auth:Oidc section exists but has no RequiredClaim
        // sub-section — _requiredClaims should stay null and the
        // policy should reduce to RequireAuthenticatedUser only.
        var sut = new OidcAuthProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        sut.AddAuthentication(services, BuildConfig(new()
        {
            ["Bowire:Auth:Oidc:Authority"] = "https://example.com/",
            ["Bowire:Auth:Oidc:ClientId"]  = "c",
            ["Bowire:Auth:Oidc:Audience"]  = "api.bowire.io",
        }));

        var pb = new AuthorizationPolicyBuilder();
        sut.BuildDefaultPolicy(pb);
        var policy = pb.Build();

        Assert.DoesNotContain(policy.Requirements,
            r => r is Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv).Build();
}
