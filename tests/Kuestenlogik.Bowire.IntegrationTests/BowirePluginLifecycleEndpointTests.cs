// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.IntegrationTests.Fakes;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Contract tests for the lifecycle endpoint at
/// <c>POST /api/plugins/{pluginId}/lifecycle/{action}</c>. Each test
/// spins up a minimal host that maps just the plugin endpoints with a
/// hand-built <see cref="BowireProtocolRegistry"/> containing the
/// in-test <see cref="FakeChannelProtocol"/> (id <c>"fake"</c>) so we
/// can assert side-effects on the live registry without depending on
/// the full discovery sweep.
/// </summary>
[Collection("BowireUserContext")]
public sealed class BowirePluginLifecycleEndpointTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly BowireProtocolRegistry? _originalRegistry;
    private readonly string _scratchDir;

    public BowirePluginLifecycleEndpointTests()
    {
        _originalStore = BowireUserContext.Current;
        _scratchDir = Path.Combine(Path.GetTempPath(),
            $"bowire-lifecycle-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        BowireUserContext.Current = new ScratchStore(_scratchDir);
        // Snapshot whatever registry was cached (may be null in this
        // test process) so we can restore it on Dispose and not leak
        // our "fake-only" snapshot into adjacent test classes.
        _originalRegistry = TryGetCachedRegistry();
        BowireDisabledPluginsStore.ResetForTests();
    }

    public void Dispose()
    {
        BowireUserContext.Current = _originalStore;
        if (_originalRegistry is not null)
        {
            BowireEndpointHelpers.SetRegistry(_originalRegistry);
        }
        else
        {
            // Internal but visible via InternalsVisibleTo? Not granted —
            // fall back to setting a fresh empty registry so cross-test
            // contamination is bounded.
            BowireEndpointHelpers.SetRegistry(new BowireProtocolRegistry());
        }
        BowireDisabledPluginsStore.ResetForTests();
        try { Directory.Delete(_scratchDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static BowireProtocolRegistry? TryGetCachedRegistry()
    {
        // GetRegistry triggers a Discover() on cache-miss; we can't
        // distinguish "fresh discover" from "real cached value" from the
        // outside, so just call it once and accept that the rebuild
        // cost falls on the first test in the class.
        return BowireEndpointHelpers.GetRegistry();
    }

    // ----- restart ----------------------------------------------------

    [Fact]
    public async Task Restart_replaces_instance_and_returns_ok()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        var registryBefore = BowireEndpointHelpers.GetRegistry();
        var instanceBefore = registryBefore.GetById("fake");
        Assert.NotNull(instanceBefore);

        using var resp = await client.PostAsync(
            new Uri("/api/plugins/fake/lifecycle/restart", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("restart", doc.RootElement.GetProperty("action").GetString());

        // The live registry should now hold a NEW instance with the
        // same id — Activator.CreateInstance returns a fresh object.
        var registryAfter = BowireEndpointHelpers.GetRegistry();
        var instanceAfter = registryAfter.GetById("fake");
        Assert.NotNull(instanceAfter);
        Assert.NotSame(instanceBefore, instanceAfter);
    }

    [Fact]
    public async Task Restart_returns_404_when_pluginId_not_registered()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.PostAsync(
            new Uri("/api/plugins/no-such-plugin/lifecycle/restart", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("urn:bowire:plugin:lifecycle-not-found",
            doc.RootElement.GetProperty("type").GetString());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    // ----- unload -----------------------------------------------------

    [Fact]
    public async Task Unload_removes_from_registry_and_persists_disable()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        Assert.NotNull(BowireEndpointHelpers.GetRegistry().GetById("fake"));

        using var resp = await client.PostAsync(
            new Uri("/api/plugins/fake/lifecycle/unload", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("wasActive").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("persisted").GetBoolean());

        // Side-effects: registry no longer carries the plugin, disabled
        // store records the id, and the on-disk persistence file exists.
        Assert.Null(BowireEndpointHelpers.GetRegistry().GetById("fake"));
        Assert.True(BowireDisabledPluginsStore.IsDisabled("fake"));
        Assert.True(File.Exists(Path.Combine(_scratchDir, "disabled-plugins.json")));
    }

    // ----- load -------------------------------------------------------

    [Fact]
    public async Task Load_brings_disabled_plugin_back_when_assembly_loaded()
    {
        // Pre-seed the disabled state: pretend the operator had already
        // unloaded "fake" in a prior session, then they click Load.
        BowireDisabledPluginsStore.Disable("fake");
        // The registry we hand to the host already reflects this — we
        // mount only the protocols the test cares about, so "fake" is
        // not registered initially.
        using var host = await BuildHost(seedRegistry: new BowireProtocolRegistry());
        var client = host.GetTestClient();

        Assert.Null(BowireEndpointHelpers.GetRegistry().GetById("fake"));

        using var resp = await client.PostAsync(
            new Uri("/api/plugins/fake/lifecycle/load", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("load", doc.RootElement.GetProperty("action").GetString());

        Assert.False(BowireDisabledPluginsStore.IsDisabled("fake"));
        Assert.NotNull(BowireEndpointHelpers.GetRegistry().GetById("fake"));
    }

    [Fact]
    public async Task Load_returns_404_when_assembly_has_no_matching_protocol()
    {
        using var host = await BuildHost(seedRegistry: new BowireProtocolRegistry());
        var client = host.GetTestClient();

        using var resp = await client.PostAsync(
            new Uri("/api/plugins/ghost-protocol/lifecycle/load", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("urn:bowire:plugin:lifecycle-not-loadable",
            doc.RootElement.GetProperty("type").GetString());
    }

    // ----- reset-storage ---------------------------------------------

    [Fact]
    public async Task ResetStorage_deletes_state_directory_when_present()
    {
        // Seed an on-disk plugin state directory in ~/.bowire/plugins/<id>/state.
        // PluginDir is hard-wired to %UserProfile%/.bowire/plugins/ —
        // the endpoint reads it directly; we mirror the layout here.
        var pluginDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".bowire", "plugins", "fake-state-test");
        var stateDir = Path.Combine(pluginDir, "state");
        Directory.CreateDirectory(stateDir);
        await File.WriteAllTextAsync(
            Path.Combine(stateDir, "marker.txt"), "x",
            TestContext.Current.CancellationToken);

        try
        {
            using var host = await BuildHost();
            var client = host.GetTestClient();

            using var resp = await client.PostAsync(
                new Uri("/api/plugins/fake-state-test/lifecycle/reset-storage", UriKind.Relative),
                content: null,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("diskCleared").GetBoolean());
            Assert.Equal("bowire_plugin_fake-state-test_",
                doc.RootElement.GetProperty("localStorageKeyPrefix").GetString());

            Assert.False(Directory.Exists(stateDir),
                $"Expected plugin state directory {stateDir} to be deleted.");
        }
        finally
        {
            // Defensive — endpoint already removed it, but tidy up any
            // remnants in case the assertion above tripped before the
            // reset path executed.
            try { Directory.Delete(pluginDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ResetStorage_returns_ok_with_diskCleared_false_when_no_state()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.PostAsync(
            new Uri("/api/plugins/no-state-plugin/lifecycle/reset-storage", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("diskCleared").GetBoolean());
    }

    // ----- unknown action --------------------------------------------

    [Fact]
    public async Task Unknown_action_returns_400_with_uniform_error_body()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.PostAsync(
            new Uri("/api/plugins/fake/lifecycle/explode", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("urn:bowire:plugin:lifecycle-unknown-action",
            doc.RootElement.GetProperty("type").GetString());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    // ----- health -----------------------------------------------------

    [Fact]
    public async Task Health_extends_response_with_lifecycle_rows()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        // Active row from the registry-seeded "fake" plugin.
        var payload = await client.GetFromJsonAsync<JsonElement>(
            "/api/plugins/health",
            TestContext.Current.CancellationToken);

        Assert.True(payload.TryGetProperty("lifecycle", out var lifecycle));
        Assert.Equal(JsonValueKind.Array, lifecycle.ValueKind);
        var rows = lifecycle.EnumerateArray().ToList();
        var fakeRow = rows.FirstOrDefault(r =>
            r.GetProperty("id").GetString() == "fake");
        Assert.NotEqual(JsonValueKind.Undefined, fakeRow.ValueKind);
        Assert.Equal("active", fakeRow.GetProperty("lifecycle").GetString());
    }

    // ----- host builders ---------------------------------------------

    private static async Task<IHost> BuildHost(BowireProtocolRegistry? seedRegistry = null)
    {
        // Seed the registry the endpoint helper reads. By default we
        // hand it a registry containing only the in-test FakeChannelProtocol
        // so tests don't touch real protocol plugins.
        seedRegistry ??= CreateFakeRegistry();
        BowireEndpointHelpers.SetRegistry(seedRegistry);

        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowirePluginEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       s.Configure<BowireOptions>(_ => { });
                       s.AddSingleton<Kuestenlogik.Bowire.Plugins.PluginUpdateCheckService>();
                   });
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    private static BowireProtocolRegistry CreateFakeRegistry()
    {
        var reg = new BowireProtocolRegistry();
        reg.Register(new FakeChannelProtocol());
        return reg;
    }

    /// <summary>
    /// Per-test <see cref="IBowireUserStore"/> pointing at the scratch
    /// directory so the disabled-plugins file lands there instead of
    /// the real <c>~/.bowire/</c>.
    /// </summary>
    private sealed class ScratchStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }
}
