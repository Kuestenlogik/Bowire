// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Helpers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;

namespace Kuestenlogik.Bowire.Tests.Helpers;

/// <summary>
/// Direct coverage for <see cref="SelfOriginCheck.IsSelfOrigin"/> — the gate
/// that keeps the workbench host's own endpoints from leaking into every
/// external <c>serverUrl</c> the operator adds. Pure static function; the only
/// collaborators are <see cref="IServer"/> + <see cref="IServerAddressesFeature"/>,
/// faked below so the host/port comparison logic (loopback aliases, wildcard
/// hosts, wildcard address forms, empty-address permissiveness) can be pinned
/// without spinning up Kestrel.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test fakes hold no unmanaged resources; Dispose is a no-op.")]
public sealed class SelfOriginCheckTests
{
    // ---- minimal fakes for the IServer → IServerAddressesFeature chain ----

    private sealed class FakeAddresses : IServerAddressesFeature
    {
        public ICollection<string> Addresses { get; } = new List<string>();
        public bool PreferHostingUrls { get; set; }
    }

    private sealed class FakeServer(params string[] addresses) : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public FakeServer Init()
        {
            var f = new FakeAddresses();
            foreach (var a in addresses) f.Addresses.Add(a);
            Features.Set<IServerAddressesFeature>(f);
            return this;
        }
        public void Dispose() { }
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // A service provider that resolves IServer only. Passing null models
    // "no IServer registered".
    private sealed class FakeServiceProvider(IServer? server) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServer) ? server : null;
    }

    private static FakeServiceProvider Sp(params string[] listening) =>
        new(new FakeServer(listening).Init());

    // ------------------------------- guards -------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_or_blank_serverUrl_is_not_self(string? url)
    {
        Assert.False(SelfOriginCheck.IsSelfOrigin(url, Sp("http://localhost:5180")));
    }

    [Fact]
    public void Null_serviceProvider_is_not_self()
    {
        Assert.False(SelfOriginCheck.IsSelfOrigin("http://localhost:5180", null));
    }

    [Fact]
    public void Unparseable_url_is_not_self()
    {
        Assert.False(SelfOriginCheck.IsSelfOrigin("::: not a url :::", Sp("http://localhost:5180")));
    }

    [Fact]
    public void Relative_url_is_not_self()
    {
        // UriKind.Absolute required — a relative path can't be an origin.
        Assert.False(SelfOriginCheck.IsSelfOrigin("/api/foo", Sp("http://localhost:5180")));
    }

    // ------------------------- permissive fallbacks -----------------------

    [Fact]
    public void No_IServer_registered_is_permissive_true()
    {
        Assert.True(SelfOriginCheck.IsSelfOrigin("http://example.com:1234", new FakeServiceProvider(null)));
    }

    [Fact]
    public void Empty_addresses_is_permissive_true()
    {
        Assert.True(SelfOriginCheck.IsSelfOrigin("http://example.com:1234", Sp()));
    }

    // --------------------------- positive matches -------------------------

    [Fact]
    public void Exact_host_and_port_match_is_self()
    {
        Assert.True(SelfOriginCheck.IsSelfOrigin("http://localhost:5180/api/foo", Sp("http://localhost:5180")));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5180", "http://localhost:5180")]
    [InlineData("http://localhost:5180", "http://127.0.0.1:5180")]
    [InlineData("http://[::1]:5180", "http://localhost:5180")]
    public void Loopback_aliases_match(string serverUrl, string listening)
    {
        Assert.True(SelfOriginCheck.IsSelfOrigin(serverUrl, Sp(listening)));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5180")]
    public void Any_host_listening_matches_same_port(string listening)
    {
        Assert.True(SelfOriginCheck.IsSelfOrigin("http://some.external.host:5180", Sp(listening)));
    }

    [Fact]
    public void Wildcard_address_form_matches_on_port()
    {
        // "http://+:5180" / "http://*:5180" don't parse as absolute Uris —
        // the port-suffix fallback path handles them.
        Assert.True(SelfOriginCheck.IsSelfOrigin("http://anything:5180", Sp("http://+:5180")));
        Assert.True(SelfOriginCheck.IsSelfOrigin("http://anything:5180", Sp("http://*:5180/")));
    }

    [Fact]
    public void Default_scheme_port_is_filled_in()
    {
        // https default port 443 — a serverUrl with no explicit port compares
        // against the listening address's explicit 443.
        Assert.True(SelfOriginCheck.IsSelfOrigin("https://localhost/api", Sp("https://localhost:443")));
    }

    // --------------------------- negative matches -------------------------

    [Fact]
    public void External_host_same_port_is_not_self()
    {
        Assert.False(SelfOriginCheck.IsSelfOrigin("http://api.example.com:5180", Sp("http://localhost:5180")));
    }

    [Fact]
    public void Same_host_different_port_is_not_self()
    {
        Assert.False(SelfOriginCheck.IsSelfOrigin("http://localhost:9999", Sp("http://localhost:5180")));
    }

    [Fact]
    public void Wildcard_address_different_port_is_not_self()
    {
        Assert.False(SelfOriginCheck.IsSelfOrigin("http://anything:9999", Sp("http://+:5180")));
    }

    [Fact]
    public void First_matching_address_among_several_wins()
    {
        Assert.True(SelfOriginCheck.IsSelfOrigin(
            "http://localhost:5180",
            Sp("http://10.0.0.5:8080", "http://localhost:5180", "http://+:9000")));
    }
}
