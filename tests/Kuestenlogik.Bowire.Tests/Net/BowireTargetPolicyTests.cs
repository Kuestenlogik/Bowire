// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Net;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.Tests.Net;

/// <summary>
/// Unit coverage for <see cref="BowireTargetPolicy"/> — which targets the
/// server-side endpoints may dial once <see cref="BowireOptions.LockServerUrl"/>
/// or <see cref="BowireOptions.AllowedServerUrls"/> is set, and that the URL
/// normalisation cannot be sidestepped with hint prefixes, case, trailing
/// slashes, default ports, userinfo or dot segments.
/// </summary>
public sealed class BowireTargetPolicyTests
{
    private static BowireOptions Locked(params string[] serverUrls)
    {
        var options = new BowireOptions { Mode = BowireMode.Standalone, LockServerUrl = true };
        foreach (var url in serverUrls) options.ServerUrls.Add(url);
        return options;
    }

    [Fact]
    public void Unlocked_default_allows_every_target()
    {
        var policy = BowireTargetPolicy.For(new BowireOptions());

        Assert.False(policy.IsEnforced);
        Assert.True(policy.Allows("http://169.254.169.254/latest/meta-data"));
        Assert.True(policy.Allows("grpc@http://10.0.0.1:5000"));
    }

    [Fact]
    public void Null_options_are_unrestricted()
    {
        Assert.Same(BowireTargetPolicy.Unrestricted, BowireTargetPolicy.For(null));
    }

    [Fact]
    public void Locked_allows_the_configured_url()
    {
        var policy = BowireTargetPolicy.For(Locked("https://api.example.com"));

        Assert.True(policy.IsEnforced);
        Assert.True(policy.Allows("https://api.example.com"));
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("https://internal.example.com")]
    [InlineData("https://api.example.com.evil.test")]
    [InlineData("http://api.example.com")]            // scheme differs
    [InlineData("https://api.example.com:8443")]      // port differs
    [InlineData("https://api.example.com@evil.test")] // allowed host as userinfo only
    [InlineData("grpc@https://evil.test")]
    [InlineData("grpcweb@http://169.254.169.254")]
    [InlineData("evil.test:443")]                     // not a URL → opaque, no match
    public void Locked_refuses_foreign_targets(string target)
    {
        var policy = BowireTargetPolicy.For(Locked("https://api.example.com"));

        Assert.False(policy.Allows(target));
    }

    [Theory]
    [InlineData("https://api.example.com/")]
    [InlineData("HTTPS://API.EXAMPLE.COM")]
    [InlineData("https://Api.Example.Com:443/")]
    [InlineData("grpc@https://api.example.com")]
    [InlineData("grpcweb@https://api.example.com:443/")]
    [InlineData("  https://api.example.com  ")]
    [InlineData("https://api.example.com/?x=1#frag")]
    public void Locked_accepts_hint_case_slash_and_default_port_variants(string target)
    {
        var policy = BowireTargetPolicy.For(Locked("https://api.example.com"));

        Assert.True(policy.Allows(target));
    }

    [Fact]
    public void Hinted_and_slashed_configuration_entries_are_normalised_too()
    {
        var policy = BowireTargetPolicy.For(Locked("grpcweb@HTTPS://API.EXAMPLE.COM:443/"));

        Assert.True(policy.Allows("https://api.example.com"));
        Assert.False(policy.Allows("https://other.example.com"));
    }

    [Theory]
    [InlineData("https://api.example.com/v1", true)]
    [InlineData("https://api.example.com/v1/", true)]
    [InlineData("https://api.example.com/v1/orders", true)]
    [InlineData("https://api.example.com/v10", false)]
    [InlineData("https://api.example.com/", false)]
    [InlineData("https://api.example.com/v1/../admin", false)]
    [InlineData("https://api.example.com/V1", false)]
    public void Entry_path_is_a_base_path(string target, bool allowed)
    {
        var policy = BowireTargetPolicy.For(Locked("https://api.example.com/v1"));

        Assert.Equal(allowed, policy.Allows(target));
    }

    [Fact]
    public void Empty_target_names_nothing_and_is_allowed()
    {
        var policy = BowireTargetPolicy.For(Locked("https://api.example.com"));

        Assert.True(policy.Allows(null));
        Assert.True(policy.Allows(""));
        Assert.True(policy.Allows("   "));
    }

    [Fact]
    public void ServerUrl_and_AllowedServerUrls_join_the_allowed_set()
    {
        var options = Locked("https://a.example.com");
        options.ServerUrl = "https://b.example.com";
        options.AllowedServerUrls.Add("rest@https://c.example.com/api");

        var policy = BowireTargetPolicy.For(options);

        Assert.True(policy.Allows("https://a.example.com"));
        Assert.True(policy.Allows("https://b.example.com"));
        Assert.True(policy.Allows("https://c.example.com/api/items"));
        Assert.False(policy.Allows("https://c.example.com/other"));
        Assert.False(policy.Allows("https://d.example.com"));
    }

    [Fact]
    public void AllowedServerUrls_alone_switches_enforcement_on()
    {
        var options = new BowireOptions { Mode = BowireMode.Standalone };
        options.AllowedServerUrls.Add("http://localhost:5000");

        var policy = BowireTargetPolicy.For(options);

        Assert.True(policy.IsEnforced);
        Assert.True(policy.Allows("http://LOCALHOST:5000/"));
        Assert.False(policy.Allows("http://localhost:5001"));
    }

    [Fact]
    public void Locked_with_nothing_configured_refuses_everything_in_standalone()
    {
        var policy = BowireTargetPolicy.For(Locked());

        Assert.True(policy.IsEnforced);
        Assert.False(policy.Allows("http://localhost:5000"));
    }

    [Fact]
    public void Embedded_mode_allows_the_hosts_own_origin()
    {
        var options = new BowireOptions { Mode = BowireMode.Embedded, LockServerUrl = true };
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("app.example.com");

        var policy = BowireTargetPolicy.For(options, ctx.Request);

        Assert.True(policy.Allows("https://app.example.com"));
        Assert.True(policy.Allows("grpc@https://app.example.com:443/"));
        Assert.False(policy.Allows("https://other.example.com"));
    }

    [Fact]
    public void Standalone_mode_does_not_allow_the_workbench_origin_implicitly()
    {
        var options = Locked("https://api.example.com");
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("localhost", 5080);

        var policy = BowireTargetPolicy.For(options, ctx.Request);

        Assert.False(policy.Allows("http://localhost:5080"));
    }

    [Fact]
    public void Non_url_entries_match_only_the_same_string()
    {
        var options = Locked("localhost:5000");

        var policy = BowireTargetPolicy.For(options);

        Assert.True(policy.Allows("LOCALHOST:5000/"));
        Assert.False(policy.Allows("localhost:5001"));
        Assert.False(policy.Allows("http://localhost:5000"));
    }

    [Fact]
    public void Non_http_schemes_compare_scheme_host_and_port()
    {
        var policy = BowireTargetPolicy.For(Locked("mqtt://broker.local:1883"));

        Assert.True(policy.Allows("MQTT://broker.local:1883"));
        Assert.False(policy.Allows("mqtt://broker.local:1884"));
        Assert.False(policy.Allows("nats://broker.local:1883"));
    }

    // ------------------------------ ForAuth ----------------------------------

    [Fact]
    public void ForAuth_is_unrestricted_by_default()
    {
        var policy = BowireTargetPolicy.ForAuth(new BowireOptions());

        Assert.False(policy.IsEnforced);
        Assert.True(policy.Allows("https://idp.example.com/token"));
    }

    [Fact]
    public void ForAuth_on_a_locked_host_allows_server_urls_plus_auth_urls()
    {
        var options = Locked("https://api.example.com");
        options.AllowedAuthUrls.Add("https://idp.example.com/realms/acme");

        var policy = BowireTargetPolicy.ForAuth(options);

        Assert.True(policy.IsEnforced);
        Assert.True(policy.Allows("https://api.example.com/oauth/token"));
        Assert.True(policy.Allows("https://IDP.example.com/realms/acme/protocol/openid-connect/token"));
        Assert.False(policy.Allows("https://idp.example.com/realms/other/token"));
        Assert.False(policy.Allows("http://169.254.169.254/latest/meta-data"));
    }

    [Fact]
    public void AllowedAuthUrls_do_not_widen_the_server_policy()
    {
        var options = Locked("https://api.example.com");
        options.AllowedAuthUrls.Add("https://idp.example.com");

        Assert.False(BowireTargetPolicy.For(options).Allows("https://idp.example.com"));
        Assert.True(BowireTargetPolicy.ForAuth(options).Allows("https://idp.example.com"));
    }

    [Fact]
    public void AllowedAuthUrls_alone_restrict_only_the_auth_helpers()
    {
        var options = new BowireOptions { Mode = BowireMode.Standalone };
        options.AllowedAuthUrls.Add("https://idp.example.com");

        Assert.False(BowireTargetPolicy.For(options).IsEnforced);
        var auth = BowireTargetPolicy.ForAuth(options);
        Assert.True(auth.IsEnforced);
        Assert.True(auth.Allows("https://idp.example.com/token"));
        Assert.False(auth.Allows("https://evil.test/token"));
    }

    [Fact]
    public void AllowedServerUrls_alone_also_restrict_the_auth_helpers()
    {
        var options = new BowireOptions { Mode = BowireMode.Standalone };
        options.AllowedServerUrls.Add("https://api.example.com");

        var auth = BowireTargetPolicy.ForAuth(options);

        Assert.True(auth.IsEnforced);
        Assert.True(auth.Allows("https://api.example.com/token"));
        Assert.False(auth.Allows("https://evil.test/token"));
    }

    // ------------------------------ remedy -----------------------------------

    [Fact]
    public void Server_remedy_names_the_server_flag_with_the_origin()
    {
        var policy = BowireTargetPolicy.For(Locked("https://api.example.com"));

        var remedy = policy.Remedy("grpc@https://user:pw@other.example.com:8443/v1/x?y=1");

        // Origin only — no hint, no userinfo, no path.
        Assert.Contains("--allowed-server-url https://other.example.com:8443 ", remedy, StringComparison.Ordinal);
        Assert.DoesNotContain("pw", remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void Auth_remedy_names_the_auth_flag_with_the_origin()
    {
        var policy = BowireTargetPolicy.ForAuth(Locked("https://api.example.com"));

        var remedy = policy.Remedy("https://login.example.com/realms/acme/protocol/openid-connect/token");

        Assert.Contains("--allowed-auth-url https://login.example.com ", remedy, StringComparison.Ordinal);
        Assert.Contains("AllowedAuthUrls", remedy, StringComparison.Ordinal);
    }
}
