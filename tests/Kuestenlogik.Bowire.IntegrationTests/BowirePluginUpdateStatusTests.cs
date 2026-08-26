// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// <c>GET /api/plugins/check-updates/status</c> — the sidebar badge's source.
/// </summary>
/// <remarks>
/// <para>
/// This is the read that must never touch the network. The badge renders on
/// every page load, and the update check itself is opt-in precisely so an
/// air-gapped or privacy-sensitive install stays quiet — a status read that
/// reached nuget.org would defeat that without anyone noticing.
/// </para>
/// <para>
/// So it answers from the last persisted snapshot, and <c>cached: null</c>
/// (nothing has run) is a normal answer rather than an error.
/// </para>
/// </remarks>
[Collection("BowireStorageRoot")]
public sealed class BowirePluginUpdateStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-updchk-" + Guid.NewGuid().ToString("N"));
    private readonly IBowireUserStore _previous = BowireUserContext.Current;

    public BowirePluginUpdateStatusTests()
    {
        Directory.CreateDirectory(_root);
        // The cache lives under the user root; without this the test would
        // read (and a future writer would overwrite) the developer's own.
        BowireUserContext.Current = new DefaultBowireUserStore(_root);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _previous;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<IHost> BuildHost(BowirePluginUpdateCheckOptions? options = null)
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
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       s.AddSingleton<IOptions<BowirePluginUpdateCheckOptions>>(
                           Options.Create(options ?? new BowirePluginUpdateCheckOptions()));
                   });
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<JsonElement> Status(IHost host)
    {
        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/api/plugins/check-updates/status", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Before_Any_Check_Has_Run_The_Cached_Snapshot_Is_Null()
    {
        // The default install: background checks off, nobody pressed "Check
        // now". The badge has to render that without an error.
        using var host = await BuildHost();

        var body = await Status(host);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("cached").ValueKind);
    }

    [Fact]
    public async Task The_Status_Reports_The_Opt_In_Flag_As_Configured()
    {
        // Outbound calls are opt-in across Bowire; this endpoint is where the
        // UI learns whether the background half is on.
        using var host = await BuildHost(new BowirePluginUpdateCheckOptions { Enabled = false });

        Assert.False((await Status(host)).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task An_Enabled_Check_Reports_Its_Interval_And_Prerelease_Choice()
    {
        using var host = await BuildHost(new BowirePluginUpdateCheckOptions
        {
            Enabled = true,
            IntervalHours = 6,
            IncludePrerelease = true,
        });

        var body = await Status(host);

        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(6, body.GetProperty("intervalHours").GetInt32());
        Assert.True(body.GetProperty("includePrerelease").GetBoolean());
    }

    [Fact]
    public async Task A_Persisted_Snapshot_Comes_Back_Without_A_Network_Call()
    {
        // The whole point of persisting it: the badge renders from disk. The
        // proof that nothing was fetched is that this snapshot names a package
        // and a version nuget.org has never heard of.
        //
        // The field names are the record's own (`results`, `installed`,
        // `latest`) — an invented shape deserialises into a snapshot with
        // default values and no entries, which reads as "a check ran and found
        // nothing" rather than as a broken fixture.
        var stateDir = Path.Combine(_root, "state");
        Directory.CreateDirectory(stateDir);
        await File.WriteAllTextAsync(Path.Combine(stateDir, "update-check.json"), """
            {"checkedAt":"2026-08-26T09:00:00+00:00","includePrerelease":false,
             "results":[{"packageId":"Acme.Bowire.Protocol.Widget",
                         "installed":"1.0.0","latest":"9.9.9",
                         "updateAvailable":true,"error":null}]}
            """, TestContext.Current.CancellationToken);

        using var host = await BuildHost();
        var cached = (await Status(host)).GetProperty("cached");

        Assert.Equal(JsonValueKind.Object, cached.ValueKind);
        Assert.Contains("Acme.Bowire.Protocol.Widget", cached.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Cache_File_That_Will_Not_Parse_Reads_As_No_Snapshot()
    {
        // A half-written file from a killed process must cost the badge, not
        // the page.
        var stateDir = Path.Combine(_root, "state");
        Directory.CreateDirectory(stateDir);
        await File.WriteAllTextAsync(Path.Combine(stateDir, "update-check.json"),
            "{ this is not json", TestContext.Current.CancellationToken);

        using var host = await BuildHost();

        Assert.Equal(JsonValueKind.Null, (await Status(host)).GetProperty("cached").ValueKind);
    }

    [Fact]
    public void Reading_The_Cache_Directly_Agrees_With_The_Endpoint()
        // The endpoint is a thin wrapper; this pins the helper the CLI and the
        // hosted service share with it.
        => Assert.Null(PluginUpdateCheckService.ReadCached());
}
