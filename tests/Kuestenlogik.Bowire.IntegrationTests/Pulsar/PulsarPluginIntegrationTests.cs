// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Pulsar;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Kuestenlogik.Bowire.IntegrationTests.Pulsar;

/// <summary>
/// Drives <see cref="BowirePulsarProtocol"/>'s HTTP-admin-driven
/// discovery against an in-process fake of the Pulsar admin REST
/// surface. The binary-protocol produce/subscribe paths still need
/// Testcontainers and live in a separate Docker-gated suite when
/// they land.
/// </summary>
public sealed class PulsarPluginIntegrationTests : IAsyncLifetime
{
    // CA1861: hoist the literal array out so MapGet's per-request
    // delegate doesn't reallocate it on every hit.
    private static readonly string[] PublicDefaultTopics =
    [
        "persistent://public/default/orders",
        "persistent://public/default/shipments",
    ];

    private WebApplication? _app;
    private string _adminBase = "";

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        // public/default → two topics
        _app.MapGet("/admin/v2/persistent/public/default", () =>
            Results.Json(PublicDefaultTopics));

        // team-a/orders → 404 (namespace doesn't exist on this broker)
        _app.MapGet("/admin/v2/persistent/team-a/orders", () => Results.NotFound());

        // junk/junk → bogus JSON shape (not a string array)
        _app.MapGet("/admin/v2/persistent/junk/junk", () =>
            Results.Json(new { not_an_array = 1 }));

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        _adminBase = addresses.First();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task DiscoverAsync_Surfaces_Topics_From_Admin_Api()
    {
        using var p = new BowirePulsarProtocol();
        // The plugin treats http://host:port as the admin URL.
        var services = await p.DiscoverAsync(_adminBase, showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, services.Count);
        Assert.Contains(services, s => s.Name == "orders");
        Assert.Contains(services, s => s.Name == "shipments");
        Assert.All(services, s =>
        {
            Assert.Equal("pulsar", s.Source);
            Assert.Equal(2, s.Methods.Count);
            Assert.Contains(s.Methods, m => m.Name == "produce");
            Assert.Contains(s.Methods, m => m.Name == "subscribe");
        });
    }

    [Fact]
    public async Task DiscoverAsync_Skips_Namespaces_That_404()
    {
        // Override the namespaces setting via reflection-free path:
        // BowirePulsarProtocol reads them from Settings → we can't
        // mutate that from outside, but the helper ParseNamespaces is
        // public, so we exercise the discovery loop directly via the
        // PulsarDiscovery surface (kept internal to the plugin asm via
        // InternalsVisibleTo to the IntegrationTests project).
        var endpoints = PulsarConnectionHelper.Resolve(_adminBase);
        Assert.NotNull(endpoints);
        using var http = new HttpClient();
        var services = await PulsarDiscovery.ListTopicsAsync(
            http, endpoints!, ["team-a/orders"], "http://x", TestContext.Current.CancellationToken);
        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Skips_Namespaces_With_Bogus_Json_Shape()
    {
        var endpoints = PulsarConnectionHelper.Resolve(_adminBase);
        using var http = new HttpClient();
        var services = await PulsarDiscovery.ListTopicsAsync(
            http, endpoints!, ["junk/junk"], "http://x", TestContext.Current.CancellationToken);
        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Skips_Blank_And_Malformed_Namespace_Entries()
    {
        var endpoints = PulsarConnectionHelper.Resolve(_adminBase);
        using var http = new HttpClient();
        // Empty string + a name with no slash → both filtered out.
        var services = await PulsarDiscovery.ListTopicsAsync(
            http, endpoints!, ["", "no-slash"], "http://x", TestContext.Current.CancellationToken);
        Assert.Empty(services);
    }

    [Fact]
    public async Task InvokeAsync_Surfaces_Connection_Error_For_Unreachable_Broker()
    {
        using var p = new BowirePulsarProtocol();
        // FullName valid; broker URL points at an unreachable port so
        // DotPulsar's connect attempt fails fast. The plugin should
        // catch the exception and return it as Status.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var ct = linked.Token;

        var result = await p.InvokeAsync(
            serverUrl: "pulsar://127.0.0.1:1",
            service: "x",
            method: "pulsar/topic/persistent://public/default/x/produce",
            jsonMessages: ["payload"],
            showInternalServices: false,
            metadata: null,
            ct: ct);

        Assert.NotEqual("OK", result.Status);
        Assert.False(string.IsNullOrEmpty(result.Status));
    }

    [Fact]
    public async Task InvokeAsync_Returns_Routing_Error_For_Non_Produce_Op()
    {
        using var p = new BowirePulsarProtocol();
        var result = await p.InvokeAsync(
            serverUrl: "pulsar://127.0.0.1:6650",
            service: "x",
            method: "pulsar/topic/foo/subscribe",
            jsonMessages: [""],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Contains("Unknown Pulsar route", result.Status);
    }

    [Fact]
    public async Task InvokeAsync_Honours_Topic_Metadata_Override()
    {
        using var p = new BowirePulsarProtocol();
        // Override the topic via metadata; broker is still unreachable
        // so we expect an error, but the route resolution itself must
        // succeed (i.e. we don't see the "Unknown route" sentinel).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var ct = linked.Token;

        var result = await p.InvokeAsync(
            serverUrl: "pulsar://127.0.0.1:1",
            service: "x",
            method: "pulsar/topic/discovery-topic/produce",
            jsonMessages: ["payload"],
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                ["topic"] = "persistent://other/ns/override-topic",
            },
            ct: ct);
        Assert.DoesNotContain("Unknown Pulsar route", result.Status);
    }

    [Fact]
    public async Task OpenChannelAsync_Returns_Null()
    {
        using var p = new BowirePulsarProtocol();
        var ch = await p.OpenChannelAsync(
            "pulsar://localhost", "x", "x/y", showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken);
        Assert.Null(ch);
    }

    [Fact]
    public void Initialize_With_Null_Service_Provider_Is_Safe()
    {
        using var p = new BowirePulsarProtocol();
        p.Initialize(null);
        Assert.Equal(2, p.Settings.Count);
    }

    [Fact]
    public async Task InvokeStreamAsync_Yields_Nothing_For_Bad_Url()
    {
        using var p = new BowirePulsarProtocol();
        var any = false;
        await foreach (var _ in p.InvokeStreamAsync("", "x",
            "pulsar/topic/x/subscribe", [], showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken))
        {
            any = true;
        }
        Assert.False(any);
    }

    [Fact]
    public async Task InvokeStreamAsync_Yields_Nothing_For_Non_Subscribe_Op()
    {
        using var p = new BowirePulsarProtocol();
        var any = false;
        await foreach (var _ in p.InvokeStreamAsync("pulsar://localhost", "x",
            "pulsar/topic/x/produce", [], showInternalServices: false,
            metadata: null, ct: TestContext.Current.CancellationToken))
        {
            any = true;
        }
        Assert.False(any);
    }
}
