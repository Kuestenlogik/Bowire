// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.AgentHub;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Tests.AgentHub;

/// <summary>
/// #128 — the Bowire hub and its agents. Agents push; the hub keeps them in
/// memory, marks the silent ones and serves the catalogue the agent
/// catalogue provider reads.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2234:Pass system uri objects instead of strings", Justification = "Relative paths against BaseAddress")]
public sealed class BowireHubTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static BowireAgentRegistration Registration(string service = "orders", string instance = "node-1", string callback = "http://node-1:5080/bowire", double? heartbeat = 10) =>
        new(service, instance, callback, "1.2.0", "team-orders", ["env:prod"],
            [new Sources.BowireCatalogueEntry("http://node-1:5080", service)], heartbeat);

    private static async Task<(WebApplication App, HttpClient Http)> Hub(params (string Key, string Value)[] settings)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        b.Configuration.AddInMemoryCollection(settings.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)));
        b.Services.AddSingleton(new BowireHubRegistry());
        var app = b.Build();
        app.MapBowireHub();
        await app.StartAsync(Ct);
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    [Fact]
    public void A_heartbeat_updates_the_agent_instead_of_adding_one()
    {
        var clock = new ManualClock();
        var registry = new BowireHubRegistry(clock);
        var first = registry.Register(Registration());
        clock.Now += TimeSpan.FromSeconds(5);
        var second = registry.Register(Registration() with { Version = "1.3.0" });

        Assert.Equal(first, second);
        var agent = Assert.Single(registry.List());
        Assert.Equal("1.3.0", agent.Version);
        Assert.Equal(clock.Now, agent.LastSeen);
        Assert.Equal(clock.Now - TimeSpan.FromSeconds(5), agent.RegisteredAt);
        Assert.StartsWith("orders-", agent.AgentId, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_instance_of_the_same_service_is_its_own_agent()
    {
        var registry = new BowireHubRegistry();
        registry.Register(Registration(instance: "node-1"));
        registry.Register(Registration(instance: "node-2"));
        Assert.Equal(2, registry.List().Count);
    }

    [Fact]
    public void A_silent_agent_goes_stale_after_three_heartbeats_and_is_dropped_after_thirty()
    {
        var clock = new ManualClock();
        var registry = new BowireHubRegistry(clock);
        registry.Register(Registration(heartbeat: 10));

        clock.Now += TimeSpan.FromSeconds(29);
        Assert.True(registry.List()[0].Live);

        clock.Now += TimeSpan.FromSeconds(2);
        var stale = Assert.Single(registry.List());
        Assert.False(stale.Live);

        clock.Now += TimeSpan.FromSeconds(270);
        Assert.Empty(registry.List());
    }

    [Fact]
    public async Task The_catalogue_carries_live_agents_in_the_shape_the_agent_provider_reads()
    {
        var (app, http) = await Hub();
        await using var _ = app;
        var response = await http.PostAsJsonAsync("/hub/agents", Registration(), Ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await http.GetStringAsync("/hub/agents/catalogue", Ct));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        var agent = Assert.Single(doc.RootElement.GetProperty("agents").EnumerateArray());
        Assert.Equal("orders", agent.GetProperty("serviceName").GetString());
        Assert.Equal("env:prod", agent.GetProperty("tags")[0].GetString());
        Assert.Equal("http://node-1:5080", agent.GetProperty("entries")[0].GetProperty("url").GetString());

        using var list = JsonDocument.Parse(await http.GetStringAsync("/hub/agents", Ct));
        var listed = Assert.Single(list.RootElement.GetProperty("agents").EnumerateArray());
        Assert.Equal("http://node-1:5080/bowire", listed.GetProperty("callbackUrl").GetString());
        Assert.True(listed.GetProperty("live").GetBoolean());
    }

    [Fact]
    public async Task With_a_token_every_hub_call_needs_it()
    {
        var (app, http) = await Hub(("Bowire:Hub:Token", "s3cret"));
        await using var _ = app;

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/hub/agents", Registration(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/hub/agents/catalogue", Ct)).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/hub/agents") { Content = JsonContent.Create(Registration()) };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer s3cret");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(request, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/bowire")]
    [InlineData("https://elsewhere.example/bowire")]
    public async Task A_callback_url_outside_http_or_the_trusted_prefixes_is_refused(string callback)
    {
        var (app, http) = await Hub(("Bowire:Hub:TrustedAgentPrefixes", "http://node-1:5080/; https://fleet.internal/"));
        await using var _ = app;
        var response = await http.PostAsJsonAsync("/hub/agents", Registration(callback: callback), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var list = JsonDocument.Parse(await http.GetStringAsync("/hub/agents", Ct));
        Assert.Empty(list.RootElement.GetProperty("agents").EnumerateArray());
    }

    [Fact]
    public async Task The_publisher_registers_heartbeats_and_deregisters()
    {
        var (app, http) = await Hub();
        await using var _ = app;
        var options = new BowireAgentOptions { HubUrl = app.Urls.First(), HeartbeatInterval = TimeSpan.FromMilliseconds(50) };
        var publisher = new BowireAgentPublisher(options, () => Registration(), NullLogger.Instance);

        publisher.Start();
        var registry = app.Services.GetRequiredService<BowireHubRegistry>();
        await WaitUntil(() => registry.List().Count == 1);
        var firstSeen = registry.List()[0].LastSeen;
        await WaitUntil(() => registry.List()[0].LastSeen > firstSeen);
        Assert.NotNull(publisher.AgentId);

        await publisher.DisposeAsync();
        Assert.Empty(registry.List());
    }

    [Fact]
    public async Task An_unreachable_hub_does_not_throw_into_the_host()
    {
        // 192.0.2.0/24 is TEST-NET-1: nothing answers there.
        var options = new BowireAgentOptions { HubUrl = "http://192.0.2.10:5080" };
        await using var publisher = new BowireAgentPublisher(options, () => Registration(), NullLogger.Instance,
            new RefusingHandler());
        Assert.False(await publisher.PushOnceAsync(Ct));
        Assert.Null(publisher.AgentId);
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    [Fact]
    public async Task A_Bowire_with_a_hub_url_registers_itself_once_it_listens()
    {
        var (hub, hubHttp) = await Hub();
        await using var _ = hub;

        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bowire:Agent:HubUrl"] = hub.Urls.First(),
            ["Bowire:Agent:ServiceName"] = "payments",
            ["Bowire:Agent:Tags"] = "env:staging, parallel-executor",
        });
        b.Services.AddBowire();
        var agent = b.Build();
        agent.MapBowire();
        await agent.StartAsync(Ct);

        var registry = hub.Services.GetRequiredService<BowireHubRegistry>();
        await WaitUntil(() => registry.List().Count == 1);
        var listed = registry.List()[0];
        Assert.Equal("payments", listed.ServiceName);
        Assert.Equal(agent.Urls.First().TrimEnd('/') + "/bowire", listed.CallbackUrl);
        Assert.Equal(["env:staging", "parallel-executor"], listed.Tags);
        Assert.Equal(agent.Urls.First().TrimEnd('/'), Assert.Single(listed.Entries).Url);

        await agent.StopAsync(Ct);
        await agent.DisposeAsync();
        Assert.Empty(registry.List());
    }

    [Fact]
    public async Task The_workbench_says_whether_it_is_a_hub()
    {
        foreach (var enabled in new[] { false, true })
        {
            var b = WebApplication.CreateSlimBuilder();
            b.Logging.ClearProviders();
            b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
            b.Configuration["Bowire:Hub:Enabled"] = enabled ? "true" : "false";
            b.Services.AddBowire();
            await using var app = b.Build();
            app.MapBowire();
            await app.StartAsync(Ct);
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            if (enabled)
            {
                var post = await http.PostAsJsonAsync("/hub/agents", Registration(), Ct);
                post.EnsureSuccessStatusCode();
            }
            else
            {
                Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/hub/agents", Registration(), Ct)).StatusCode);
            }
            using var doc = JsonDocument.Parse(await http.GetStringAsync("/bowire/api/hub/agents", Ct));
            Assert.Equal(enabled, doc.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Equal(enabled ? 1 : 0, doc.RootElement.GetProperty("agents").GetArrayLength());
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:5080", "/bowire", "http://127.0.0.1:5080/bowire")]
    [InlineData("http://localhost:5080/", "", "http://localhost:5080")]
    [InlineData("http://[::1]:5080", "/bowire", "http://[::1]:5080/bowire")]
    public void The_callback_url_is_the_listening_address_plus_the_route(string address, string basePath, string expected)
    {
        Assert.Equal(expected, BowireAgentPublisher.DeriveCallbackUrl([address], basePath));
    }

    [Fact]
    public void A_wildcard_address_becomes_the_machine_name_and_https_wins()
    {
        Assert.Equal($"https://{Environment.MachineName}:5443/bowire",
            BowireAgentPublisher.DeriveCallbackUrl(["http://0.0.0.0:5080", "https://[::]:5443"], "/bowire"));
        Assert.Null(BowireAgentPublisher.DeriveCallbackUrl([], "/bowire"));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("condition not met in time");
            await Task.Delay(20, Ct);
        }
    }
}
