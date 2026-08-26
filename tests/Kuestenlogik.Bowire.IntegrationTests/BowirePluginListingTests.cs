// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// <c>GET /api/plugins</c> — the list the in-UI plugin browser renders.
/// </summary>
/// <remarks>
/// <para>
/// This walks the plugin directory and reads each <c>plugin.json</c>, so what
/// matters is how it behaves against directories it did not write: a manifest
/// that will not parse, one missing the fields the UI shows, a directory with
/// no manifest at all. Any of those throwing would empty the plugin browser
/// for every plugin, not just the broken one.
/// </para>
/// <para>
/// Testable at all because #616 routed <c>PluginDir</c> through
/// <see cref="BowirePaths"/> — before that it was a static field built from
/// the user profile, and a test could only have run against the developer's
/// real <c>~/.bowire/plugins</c>.
/// </para>
/// </remarks>
[Collection("CwdSerialised")]
public sealed class BowirePluginListingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-plugins-" + Guid.NewGuid().ToString("N"));
    private readonly IBowirePathResolver _previous = BowirePaths.Current;

    public BowirePluginListingTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
        // Point every scope at the temp tree — the same override a fixture
        // would use via BOWIRE_DATA_DIR, applied in-process.
        BowirePaths.Current = new BowirePathResolver(
            name => name == BowirePathResolver.DataDirVariable ? _root : null,
            () => _root);
    }

    public void Dispose()
    {
        BowirePaths.Current = _previous;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string AddPlugin(string id, string? manifestJson)
    {
        var dir = Path.Combine(_root, "plugins", id);
        Directory.CreateDirectory(dir);
        // A dll so the row's file count has something to report.
        File.WriteAllText(Path.Combine(dir, id + ".dll"), "not a real assembly");
        if (manifestJson is not null)
            File.WriteAllText(Path.Combine(dir, "plugin.json"), manifestJson);
        return dir;
    }

    private static async Task<IHost> BuildHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowirePluginEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<JsonElement> GetPlugins(IHost host)
    {
        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/api/plugins", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.Clone();
    }

    private static JsonElement.ArrayEnumerator Rows(JsonElement body)
        => body.GetProperty("plugins").EnumerateArray();

    /// <summary>
    /// Just the rows this test put on disk.
    /// </summary>
    /// <remarks>
    /// The listing also carries every bundled protocol plugin the registry
    /// found — 21 of them on a CI runner — so asserting on the whole set
    /// would be asserting on how many protocols ship, which is not what any
    /// of this is about.
    /// </remarks>
    private static List<JsonElement> Own(JsonElement body, params string[] ids)
        => Rows(body)
            .Where(r => r.TryGetProperty("packageId", out var id)
                     && ids.Contains(id.GetString(), StringComparer.Ordinal))
            .ToList();

    [Fact]
    public async Task An_Empty_Plugin_Directory_Lists_Nothing_Rather_Than_Failing()
    {
        // The plugin browser asks for this on open; an error here would take
        // the pane down on a fresh install.
        using var host = await BuildHost();

        var body = await GetPlugins(host);

        Assert.Equal(JsonValueKind.Array, body.GetProperty("plugins").ValueKind);
        // Bundled protocols are still listed; what must be absent is anything
        // from the (empty) plugin directory.
        Assert.Empty(Own(body, "Acme.Bowire.Protocol.Widget", "Good.Plugin", "Broken.Plugin"));
    }

    [Fact]
    public async Task A_Manifest_Supplies_The_Labels_The_Browser_Shows()
    {
        AddPlugin("Acme.Bowire.Protocol.Widget", """
            {"packageId":"Acme.Bowire.Protocol.Widget","displayName":"Widget","description":"Speaks widget.","version":"1.2.3"}
            """);
        using var host = await BuildHost();

        var row = Assert.Single(Own(await GetPlugins(host), "Acme.Bowire.Protocol.Widget"));

        Assert.Equal("Widget", row.GetProperty("displayName").GetString());
        Assert.Equal("Speaks widget.", row.GetProperty("description").GetString());
        Assert.Equal("1.2.3", row.GetProperty("version").GetString());
    }

    [Fact]
    public async Task A_Manifest_That_Will_Not_Parse_Costs_Its_Own_Row_And_No_Others()
    {
        // The failure mode worth preventing: one hand-edited plugin.json
        // emptying the whole plugin browser.
        AddPlugin("Broken.Plugin", "{ this is not json");
        AddPlugin("Good.Plugin", """{"packageId":"Good.Plugin","displayName":"Good"}""");

        using var host = await BuildHost();
        var rows = Own(await GetPlugins(host), "Broken.Plugin", "Good.Plugin");

        // The unparseable one is skipped — deliberately, per the `continue` in
        // the handler — and the healthy one is unaffected. That is the whole
        // point: one hand-edited plugin.json costs its own row and nobody
        // else's. (Its absence is not silent: /api/plugins/health is where a
        // plugin that failed to load says why.)
        var row = Assert.Single(rows);
        Assert.Equal("Good", row.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task A_Directory_With_No_Manifest_Still_Appears()
    {
        // A plugin copied in by hand, or one whose manifest an installer has
        // not written yet. Hiding it would make "I installed it and it is not
        // there" impossible to diagnose from the UI.
        AddPlugin("Manifestless.Plugin", manifestJson: null);
        using var host = await BuildHost();

        var row = Assert.Single(Own(await GetPlugins(host), "Manifestless.Plugin"));

        Assert.Contains("Manifestless.Plugin",
            row.GetProperty("packageId").GetString()!, StringComparison.Ordinal);
        // The file count is what tells an operator whether anything is in there.
        Assert.True(row.GetProperty("files").GetInt32() >= 1);
    }

    [Fact]
    public async Task A_Manifest_With_Blank_Labels_Does_Not_Render_Blank_Rows()
    {
        // An empty string is not the same as an absent field, and the UI
        // shows both as a nameless row unless something fills them in.
        AddPlugin("Blank.Plugin", """{"packageId":"Blank.Plugin","displayName":"","description":""}""");
        using var host = await BuildHost();

        var row = Assert.Single(Own(await GetPlugins(host), "Blank.Plugin"));

        // Nothing in the registry answers to this assembly name, so the
        // fallback has nothing to offer — but the row must still be there
        // with its id, rather than being dropped.
        Assert.Equal("Blank.Plugin", row.GetProperty("packageId").GetString());
    }

    [Fact]
    public async Task Several_Plugins_All_Appear()
    {
        AddPlugin("A.Plugin", """{"packageId":"A.Plugin","displayName":"A"}""");
        AddPlugin("B.Plugin", """{"packageId":"B.Plugin","displayName":"B"}""");
        AddPlugin("C.Plugin", null);

        using var host = await BuildHost();

        Assert.Equal(3, Own(await GetPlugins(host), "A.Plugin", "B.Plugin", "C.Plugin").Count);
    }

    [Fact]
    public async Task The_Response_Carries_Extensions_Alongside_Plugins()
    {
        // The browser renders two sections from one call; a client indexing
        // the top level as an array would break on this shape.
        using var host = await BuildHost();

        var body = await GetPlugins(host);

        Assert.True(body.TryGetProperty("plugins", out _));
        Assert.True(body.TryGetProperty("extensions", out var ext));
        Assert.Equal(JsonValueKind.Array, ext.ValueKind);
    }

    [Fact]
    public async Task Plugin_Health_Is_An_Array_Before_Anything_Has_Loaded()
    {
        // Operators land here from "0 services" to find out why. Empty is a
        // valid answer; an error is not.
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/api/plugins/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // An envelope, like the listing: room to add fields without breaking a
        // client that indexes the top level.
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task Protocols_Lists_What_The_Registry_Discovered()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/api/plugins/protocols", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
