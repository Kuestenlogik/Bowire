// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end coverage for the discovery-diagnostics contract (#534):
/// when <c>/api/services</c> finds nothing, its problem+json body must
/// account for EVERY probed plugin — not just the ones that threw.
/// <para>
/// The regression this pins is concrete: the old endpoint built its
/// <c>attempts</c> array out of the error list alone, so a plugin that
/// ran cleanly and returned nothing simply did not appear. An operator
/// staring at an empty sidebar could not tell "REST never got a turn"
/// from "REST looked and there was no OpenAPI document".
/// </para>
/// </summary>
// Same collection as the other registry-injecting suites: SetRegistry
// writes a process-wide static, so these must not run concurrently with
// tests that expect the real plugin set.
[Collection(nameof(RestInvokerEndToEndFixture))]
public sealed class BowireDiscoveryDiagnosticsTests
{
    private const string TargetUrl = "https://api.example.com";

    [Fact]
    public async Task Services_502_Lists_Every_Probed_Plugin_Including_The_Silent_One()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new ThrowingProtocol("boom", "Boom"));
        registry.Register(new SilentProtocol("quiet", "Quiet"));

        await using var host = await StartAsync(registry);
        using var doc = await GetProblemAsync(host, HttpStatusCode.BadGateway,
            $"/bowire/api/services?serverUrl={Uri.EscapeDataString(TargetUrl)}");

        var attempts = doc.RootElement.GetProperty("attempts").EnumerateArray().ToList();
        Assert.Equal(2, attempts.Count);

        var boom = attempts.Single(a => a.GetProperty("pluginId").GetString() == "boom");
        Assert.Equal("error", boom.GetProperty("outcome").GetString());
        Assert.Equal("Boom", boom.GetProperty("plugin").GetString());
        // Raw exception text, no "Boom: " prefix — the record carries the
        // plugin name in its own field.
        Assert.Equal("discovery boom", boom.GetProperty("message").GetString());

        var quiet = attempts.Single(a => a.GetProperty("pluginId").GetString() == "quiet");
        Assert.Equal("empty", quiet.GetProperty("outcome").GetString());
        Assert.Equal(0, quiet.GetProperty("servicesFound").GetInt32());

        Assert.Equal("urn:bowire:discovery:no-match", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(TargetUrl, doc.RootElement.GetProperty("serverUrl").GetString());
        Assert.Contains("protocol@", doc.RootElement.GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Services_502_Reports_Attempts_When_Every_Plugin_Ran_Clean()
    {
        // No plugin threw, so this takes the "completed without errors but
        // returned no services" branch — which used to emit an ad-hoc
        // "{Name}: returned no services" string list instead of the real
        // attempt records.
        var registry = new BowireProtocolRegistry();
        registry.Register(new SilentProtocol("quiet", "Quiet"));
        registry.Register(new SilentProtocol("hush", "Hush"));

        await using var host = await StartAsync(registry);
        using var doc = await GetProblemAsync(host, HttpStatusCode.BadGateway,
            $"/bowire/api/services?serverUrl={Uri.EscapeDataString(TargetUrl)}");

        var attempts = doc.RootElement.GetProperty("attempts").EnumerateArray().ToList();
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, a => Assert.Equal("empty", a.GetProperty("outcome").GetString()));
        Assert.All(attempts, a => Assert.True(a.GetProperty("durationMs").GetInt64() >= 0));
    }

    [Fact]
    public async Task Services_502_Narrows_Attempts_To_The_Hinted_Plugin()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new ThrowingProtocol("boom", "Boom"));
        registry.Register(new SilentProtocol("quiet", "Quiet"));

        await using var host = await StartAsync(registry);
        using var doc = await GetProblemAsync(host, HttpStatusCode.BadGateway,
            $"/bowire/api/services?serverUrl={Uri.EscapeDataString("boom@" + TargetUrl)}");

        var attempt = Assert.Single(doc.RootElement.GetProperty("attempts").EnumerateArray().ToList());
        Assert.Equal("boom", attempt.GetProperty("pluginId").GetString());
    }

    [Fact]
    public async Task Services_502_With_No_Plugins_Still_Carries_An_Empty_Attempts_Array()
    {
        // Shape consistency: every no-services body has `attempts`, so the
        // frontend renders one code path instead of branching on presence.
        await using var host = await StartAsync(new BowireProtocolRegistry());
        using var doc = await GetProblemAsync(host, HttpStatusCode.BadGateway,
            $"/bowire/api/services?serverUrl={Uri.EscapeDataString(TargetUrl)}");

        Assert.Equal("urn:bowire:discovery:no-plugins", doc.RootElement.GetProperty("type").GetString());
        Assert.Empty(doc.RootElement.GetProperty("attempts").EnumerateArray().ToList());
    }

    private static async Task<JsonDocument> GetProblemAsync(
        DiagnosticsHost host, HttpStatusCode expected, string path)
    {
        var response = await host.Client.GetAsync(new Uri(path, UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(body);
    }

    private static async Task<DiagnosticsHost> StartAsync(BowireProtocolRegistry registry)
    {
        // ProtoUploadStore is a process-wide static, and an earlier suite
        // (EndpointCoverageTests.ProtoUpload_ValidContent_…) leaves an
        // uploaded .proto behind. /api/services returns those with 200
        // before it ever reaches the no-services triage, so without this
        // the whole class passes in isolation and fails in a full run.
        ProtoUploadStore.Clear();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapBowire("/bowire");
        // MapBowire caches the real (assembly-scanned) registry; swap in the
        // fake afterwards so the fanout probes exactly the stubs above.
        Endpoints.BowireEndpointHelpers.SetRegistry(registry);

        await app.StartAsync(TestContext.Current.CancellationToken);
        return new DiagnosticsHost(app, app.GetTestClient());
    }

    private sealed class DiagnosticsHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
            // Drop the fake so later readers rediscover the real plugin set.
            Endpoints.BowireEndpointHelpers.ResetRegistry();
        }
    }

    private sealed class ThrowingProtocol(string id, string name) : StubProtocol(id, name)
    {
        public override Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => throw new InvalidOperationException("discovery boom");
    }

    private sealed class SilentProtocol(string id, string name) : StubProtocol(id, name)
    {
        public override Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());
    }

    private abstract class StubProtocol(string id, string name) : IBowireProtocol
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string IconSvg => "<svg/>";

        public abstract Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default);

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", new Dictionary<string, string>()));

        public IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<string>();

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }
}
