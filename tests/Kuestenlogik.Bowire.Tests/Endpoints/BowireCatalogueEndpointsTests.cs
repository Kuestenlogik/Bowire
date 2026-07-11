// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// Integration coverage for <see cref="BowireCatalogueEndpoints"/> — the
/// six-route catalogue surface (#136 / #309). Drives a loopback Kestrel host
/// so the entries / refresh fetch path, the config CRUD, and the secret
/// masking + merge helpers all execute end-to-end. Joins CwdSerialised
/// because the override store is redirected via the
/// BOWIRE_CATALOGUE_CONFIG_PATH env var (see the [[reference_cwd_serialised_collection]]
/// convention).
/// </summary>
[Collection("CwdSerialised")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope — app + client disposed by the caller.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic.")]
public sealed class BowireCatalogueEndpointsTests
{
    private const string ConfigPathEnv = "BOWIRE_CATALOGUE_CONFIG_PATH";
    private static readonly Uri ConfigUri = new("/api/catalogue/config", UriKind.Relative);
    private static readonly Uri RefreshUri = new("/api/catalogue/refresh", UriKind.Relative);

    private sealed record Host(WebApplication App, HttpClient Http, string CatalogueFile, string ConfigFile, string? PrevEnv)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.DisposeAsync().ConfigureAwait(false);
            Environment.SetEnvironmentVariable(ConfigPathEnv, PrevEnv);
            foreach (var f in new[] { CatalogueFile, ConfigFile })
            {
                try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { /* best-effort */ }
            }
        }
    }

    private static async Task<Host> StartAsync(CancellationToken ct, bool wireCatalogue = true)
    {
        var catFile = Path.Combine(Path.GetTempPath(), "bowire-cat-" + Guid.NewGuid().ToString("N") + ".json");
        var cfgFile = Path.Combine(Path.GetTempPath(), "bowire-catcfg-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(catFile, """
            { "version": 1, "entries": [ { "url": "https://a.example.com", "name": "A" } ] }
            """, ct).ConfigureAwait(false);

        var prev = Environment.GetEnvironmentVariable(ConfigPathEnv);
        Environment.SetEnvironmentVariable(ConfigPathEnv, cfgFile);

        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        if (wireCatalogue)
        {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Bowire:Discovery:Catalogue:Provider"] = "local",
                    ["Bowire:Discovery:Catalogue:Local:Path"] = catFile,
                })
                .Build();
            b.Services.AddBowireCatalogue(cfg);
        }
        var app = b.Build();
        app.MapBowireCatalogueEndpoints("");
        await app.StartAsync(ct).ConfigureAwait(false);
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return new Host(app, http, catFile, cfgFile, prev);
    }

    private static async Task<JsonElement> GetJson(HttpClient http, string path, CancellationToken ct)
    {
        using var resp = await http.GetAsync(new Uri(path, UriKind.Relative), ct);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    // ------------------------------- info -------------------------------

    [Fact]
    public async Task Info_reports_active_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var info = await GetJson(host.Http, "/api/catalogue/info", ct);
        Assert.True(info.GetProperty("available").GetBoolean());
        Assert.Equal("local", info.GetProperty("providerId").GetString());
        Assert.Equal("editable", info.GetProperty("visibility").GetString());
        Assert.False(info.GetProperty("hasOverride").GetBoolean());
    }

    [Fact]
    public async Task Info_reports_unavailable_when_catalogue_not_wired()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, wireCatalogue: false);

        var info = await GetJson(host.Http, "/api/catalogue/info", ct);
        Assert.False(info.GetProperty("available").GetBoolean());
    }

    // ---------------------------- entries / refresh ----------------------------

    [Fact]
    public async Task Entries_returns_provider_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var body = await GetJson(host.Http, "/api/catalogue/entries", ct);
        Assert.Equal("local", body.GetProperty("providerId").GetString());
        var entries = body.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("https://a.example.com", entries[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task Refresh_returns_provider_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        using var resp = await host.Http.PostAsync(RefreshUri, content: null, ct);
        resp.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
        Assert.Equal("local", body.GetProperty("providerId").GetString());
    }

    [Fact]
    public async Task Entries_returns_empty_when_catalogue_not_wired()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, wireCatalogue: false);

        var body = await GetJson(host.Http, "/api/catalogue/entries", ct);
        Assert.Equal(0, body.GetProperty("entries").GetArrayLength());
    }

    // ------------------------------- config CRUD -------------------------------

    [Fact]
    public async Task Config_get_reports_no_override_initially()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var cfg = await GetJson(host.Http, "/api/catalogue/config", ct);
        Assert.False(cfg.GetProperty("hasOverride").GetBoolean());
    }

    [Fact]
    public async Task Config_post_sets_override_and_masks_secrets()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        // Every secret-bearing sub-config → exercises all four Mask* helpers.
        var payload = """
            {
              "provider": "http",
              "http": { "url": "https://c.example.com", "authorization": "Bearer tok" },
              "consul": { "address": "http://consul:8500", "token": "acl-secret" },
              "kubernetes": { "apiServerUrl": "https://k8s:6443", "token": "k8s-secret", "caCertificatePem": "PEMDATA" },
              "agent": { "hubUrl": "https://hub", "bootstrapToken": "boot-secret" }
            }
            """;
        using (var resp = await host.Http.PostAsync(ConfigUri,
            new StringContent(payload, Encoding.UTF8, "application/json"), ct))
        {
            resp.EnsureSuccessStatusCode();
            var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
            Assert.True(body.GetProperty("hasOverride").GetBoolean());
            Assert.Equal("http", body.GetProperty("providerId").GetString());
        }

        var cfg = await GetJson(host.Http, "/api/catalogue/config", ct);
        Assert.True(cfg.GetProperty("hasOverride").GetBoolean());
        Assert.Equal("__set__", cfg.GetProperty("http").GetProperty("authorization").GetString());
        Assert.Equal("__set__", cfg.GetProperty("consul").GetProperty("token").GetString());
        Assert.Equal("__set__", cfg.GetProperty("kubernetes").GetProperty("token").GetString());
        Assert.Equal("__set__", cfg.GetProperty("kubernetes").GetProperty("caCertificatePem").GetString());
        Assert.Equal("__set__", cfg.GetProperty("agent").GetProperty("bootstrapToken").GetString());
    }

    [Fact]
    public async Task Config_post_with_blank_secret_keeps_existing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        // First save sets the token.
        using (var r1 = await host.Http.PostAsync(ConfigUri,
            new StringContent("""{ "provider": "consul", "consul": { "address": "http://consul:8500", "token": "first" } }""",
                Encoding.UTF8, "application/json"), ct))
        {
            r1.EnsureSuccessStatusCode();
        }

        // Second save leaves the token blank → MergeSecrets keeps the stored one.
        using (var r2 = await host.Http.PostAsync(ConfigUri,
            new StringContent("""{ "provider": "consul", "consul": { "address": "http://consul:8500", "token": "" } }""",
                Encoding.UTF8, "application/json"), ct))
        {
            r2.EnsureSuccessStatusCode();
        }

        var cfg = await GetJson(host.Http, "/api/catalogue/config", ct);
        // Still masked as set — the blank didn't wipe it.
        Assert.Equal("__set__", cfg.GetProperty("consul").GetProperty("token").GetString());
    }

    [Fact]
    public async Task Config_delete_clears_override()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        using (var post = await host.Http.PostAsync(ConfigUri,
            new StringContent("""{ "provider": "http", "http": { "url": "https://x" } }""", Encoding.UTF8, "application/json"), ct))
        {
            post.EnsureSuccessStatusCode();
        }

        using var del = await host.Http.DeleteAsync(ConfigUri, ct);
        del.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await del.Content.ReadAsStringAsync(ct)).RootElement;
        Assert.False(body.GetProperty("hasOverride").GetBoolean());

        var cfg = await GetJson(host.Http, "/api/catalogue/config", ct);
        Assert.False(cfg.GetProperty("hasOverride").GetBoolean());
    }

    [Fact]
    public async Task Config_post_returns_404_when_store_not_wired()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, wireCatalogue: false);

        using var resp = await host.Http.PostAsync(ConfigUri,
            new StringContent("""{ "provider": "http" }""", Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Config_post_returns_400_on_malformed_body()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        using var resp = await host.Http.PostAsync(ConfigUri,
            new StringContent("{ this is not json", Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
