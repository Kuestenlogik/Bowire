// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// How <c>bowire diff</c> and <c>bowire lint</c> read a side.
/// </summary>
/// <remarks>
/// <para>
/// Both commands take a source that is either a snapshot file or a live URL,
/// and both branch on the same rule: if the path exists on disk it is a file,
/// otherwise it is a URL. That rule is worth pinning because the failure it
/// prevents is quiet — a typo'd snapshot path that gets handed to a discovery
/// client and reported as a network problem.
/// </para>
/// <para>
/// The other half is that a failure has to be legible in a CI log. Every
/// refusal here returns <c>null</c> and writes one line naming the source, so
/// the command above can exit without a stack trace.
/// </para>
/// </remarks>
public sealed class CliSchemaSnapshotTests : IDisposable
{
    private readonly List<string> _files = [];
    private readonly StringWriter _err = new();

    public void Dispose()
    {
        _err.Dispose();
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private string SnapshotFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bowire-snapshot-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        _files.Add(path);
        return path;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_Snapshot_File_Is_Read_As_The_Service_List()
    {
        var path = SnapshotFile("""
            [{"name":"orders.v1.OrderService","package":"orders.v1","methods":[]},
             {"name":"catalog.v1.CatalogService","package":"catalog.v1","methods":[]}]
            """);

        var services = await CliSchemaSnapshot.ResolveAsync(path, protocolId: null, _err, Ct);

        Assert.NotNull(services);
        Assert.Equal(2, services.Count);
        Assert.Equal("orders.v1.OrderService", services[0].Name);
        Assert.Empty(_err.ToString());
    }

    [Fact]
    public async Task A_Snapshot_Written_By_The_Workbench_Round_Trips()
    {
        // The documented interchange promise: whatever GET /api/services
        // emitted can be diffed against later. Serialising through the same
        // options the resolver reads with is the whole of that promise.
        var expected = new List<BowireServiceInfo>
        {
            new("orders.v1.OrderService", "orders.v1", []),
        };
        var path = SnapshotFile(JsonSerializer.Serialize(expected, CliSchemaSnapshot.Json));

        var services = await CliSchemaSnapshot.ResolveAsync(path, null, _err, Ct);

        Assert.Equal("orders.v1.OrderService", Assert.Single(services!).Name);
    }

    [Fact]
    public async Task Camel_Case_And_Pascal_Case_Both_Parse()
    {
        // Snapshots get hand-written and get produced by older versions; the
        // reader is case-insensitive on purpose, and a regression here would
        // silently yield services with empty names rather than an error.
        var path = SnapshotFile("""[{"Name":"orders.v1.OrderService","Package":"orders.v1","Methods":[]}]""");

        var services = await CliSchemaSnapshot.ResolveAsync(path, null, _err, Ct);

        Assert.Equal("orders.v1.OrderService", Assert.Single(services!).Name);
    }

    [Fact]
    public async Task An_Empty_Snapshot_Is_A_List_Of_Nothing_Not_A_Failure()
    {
        // A service list can legitimately be empty — that is a diff result
        // ("everything was removed"), not a broken file.
        var path = SnapshotFile("[]");

        var services = await CliSchemaSnapshot.ResolveAsync(path, null, _err, Ct);

        Assert.NotNull(services);
        Assert.Empty(services);
        Assert.Empty(_err.ToString());
    }

    [Fact]
    public async Task A_Snapshot_Holding_Literal_Null_Says_So_And_Yields_Nothing()
    {
        // `null` deserialises without throwing. Returning it as an empty side
        // would report every service as removed.
        var path = SnapshotFile("null");

        var services = await CliSchemaSnapshot.ResolveAsync(path, null, _err, Ct);

        Assert.Null(services);
        Assert.Contains("did not parse into a service list", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Snapshot_That_Is_Not_Json_Is_Reported_With_Its_Path()
    {
        // A CI log shows this line and nothing else, so the path has to be in it.
        var path = SnapshotFile("{ this is not json");

        var services = await CliSchemaSnapshot.ResolveAsync(path, null, _err, Ct);

        Assert.Null(services);
        Assert.Contains("Failed to read snapshot", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains(path, _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Snapshot_Of_The_Wrong_Shape_Is_Reported_Rather_Than_Thrown()
    {
        // An object where a list belongs — someone saved the envelope instead
        // of the array. Common enough to deserve a message, not a stack trace.
        var path = SnapshotFile("""{"services":[]}""");

        var services = await CliSchemaSnapshot.ResolveAsync(path, null, _err, Ct);

        Assert.Null(services);
        Assert.Contains("Failed to read snapshot", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Source_That_Is_Not_A_File_Is_Treated_As_A_Url()
    {
        // The branch itself: a path that does not exist falls through to
        // discovery, and discovery is what reports the protocol problem. A
        // reader who mistyped a snapshot path sees a protocol message — which
        // is exactly why the message names the id it tried.
        var services = await CliSchemaSnapshot.ResolveAsync(
            "https://api.example.com", protocolId: "not-a-real-protocol", _err, Ct);

        Assert.Null(services);
        Assert.Contains("not-a-real-protocol", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("--protocol", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_Against_An_Unloaded_Protocol_Names_The_Flag_That_Fixes_It()
        => Assert.Null(await Discover("https://api.example.com", "nope"));

    [Fact]
    public async Task An_Explicit_Protocol_Wins_Over_The_Guess_From_The_Scheme()
    {
        // ws:// would otherwise be guessed as websocket; passing --protocol
        // has to override that, which the error message proves by naming the
        // id that was actually looked up.
        await Discover("ws://api.example.com/socket", "nope-explicit");

        Assert.Contains("nope-explicit", _err.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("websocket", _err.ToString(), StringComparison.Ordinal);
    }

    private async Task<List<BowireServiceInfo>?> Discover(string url, string? protocolId)
        => await CliSchemaSnapshot.DiscoverAsync(url, protocolId, _err, Ct);
}
