// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Schemas;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// <c>bowire diff</c> — the schema half of the PR bot.
/// </summary>
/// <remarks>
/// <para>
/// A PR check gates on the exit code and a reviewer reads the rendered output,
/// so those two are the contract. The exit code in particular is the whole
/// point of <c>--fail-on</c>: a job configured to fail on breaking changes and
/// a diff that quietly returns 0 is a removed endpoint shipping unnoticed.
/// </para>
/// <para>
/// Both sides are snapshot files here — the same service-list JSON
/// <c>GET /api/services</c> emits — so nothing in this suite reaches a network.
/// </para>
/// </remarks>
public sealed class DiffCommandTests : IDisposable
{
    private readonly List<string> _files = [];
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BowireMethodInfo Method(string service, string name, string input = "Request")
        => new(name, $"{service}.{name}", false, false,
            new BowireMessageInfo(input, $"{service}.{input}", []),
            new BowireMessageInfo("Response", $"{service}.Response", []),
            "unary");

    private static BowireServiceInfo Service(string name, params BowireMethodInfo[] methods)
        => new(name, "pkg", [.. methods]);

    private string Snapshot(params BowireServiceInfo[] services)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bowire-diff-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(services.ToList(), CliSchemaSnapshot.Json));
        _files.Add(path);
        return path;
    }

    private string OutputPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bowire-diff-out-{Guid.NewGuid():N}.md");
        _files.Add(path);
        return path;
    }

    private Task<int> Diff(
        string? baseSource, string? headSource, string? format = null,
        string? output = null, string failOn = "none")
        => DiffCommand.RunDiffAsync(
            baseSource, headSource, format, output, failOn, protocolId: null, Ct, _out, _err);

    // ---- the arguments ----

    [Fact]
    public async Task Diffing_With_Only_One_Side_Prints_The_Usage_And_Exits_Two()
    {
        // Exit 2 rather than 1: a CI job can tell "you invoked me wrong" from
        // "the diff found something".
        var exit = await Diff(baseSource: Snapshot(), headSource: null);

        Assert.Equal(2, exit);
        Assert.Contains("--base", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("--head", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Side_That_Cannot_Be_Read_Fails_Without_Rendering_A_Diff()
    {
        // Half a diff is worse than none: an unreadable base would otherwise
        // look like "everything was added".
        var path = Path.Combine(Path.GetTempPath(), $"bowire-diff-broken-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        _files.Add(path);

        var exit = await Diff(path, Snapshot());

        Assert.Equal(1, exit);
        Assert.Empty(_out.ToString());
    }

    // ---- what it renders ----

    [Fact]
    public async Task Two_Identical_Snapshots_Render_A_Diff_With_Nothing_In_It()
    {
        var before = Snapshot(Service("orders.v1.OrderService", Method("orders.v1.OrderService", "GetOrder")));

        var exit = await Diff(before, before);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(_out.ToString());
        Assert.False(doc.RootElement.GetProperty("callableMoved").GetBoolean());
    }

    [Fact]
    public async Task Json_Is_The_Default_Format()
    {
        var before = Snapshot();
        var after = Snapshot(Service("orders.v1.OrderService"));

        await Diff(before, after);

        // Parses as JSON — which markdown would not.
        using var doc = JsonDocument.Parse(_out.ToString());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task Markdown_Is_What_A_Reviewer_Reads_In_A_Pr_Comment()
    {
        var before = Snapshot(Service("orders.v1.OrderService", Method("orders.v1.OrderService", "GetOrder")));
        var after = Snapshot(Service("orders.v1.OrderService"));

        await Diff(before, after, format: "markdown");

        // Bold labels rather than headings: the comment is embedded in a PR
        // body that already has a heading structure of its own.
        var text = _out.ToString();
        Assert.Contains("**API schema:**", text, StringComparison.Ordinal);
        Assert.Contains("GetOrder", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Format_Flag_Ignores_Casing()
        // CI files are written by hand; "Markdown" must not silently produce JSON.
        => Assert.Equal(0, await Diff(Snapshot(), Snapshot(), format: "MARKDOWN"));

    [Fact]
    public async Task Writing_To_A_File_Says_Where_It_Went_And_How_Big_It_Is()
    {
        // The stdout line is all a CI log shows; without it a job that wrote
        // an empty report looks identical to one that wrote a full one.
        var path = OutputPath();

        await Diff(Snapshot(), Snapshot(Service("orders.v1.OrderService")), format: "markdown", output: path);

        Assert.True(File.Exists(path));
        Assert.Contains(path, _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("chars", _out.ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(await File.ReadAllTextAsync(path, Ct));
    }

    // ---- the exit code the PR check gates on ----

    [Fact]
    public async Task A_Removed_Method_Fails_A_Check_Set_To_Fail_On_Breaking()
    {
        var before = Snapshot(Service("orders.v1.OrderService", Method("orders.v1.OrderService", "GetOrder")));
        var after = Snapshot(Service("orders.v1.OrderService"));

        Assert.Equal(1, await Diff(before, after, failOn: "breaking"));
    }

    [Fact]
    public async Task An_Added_Method_Does_Not_Fail_A_Breaking_Check()
    {
        // Additive changes are the ones teams ship daily; failing on those
        // would train everyone to pass --fail-on none.
        var before = Snapshot(Service("orders.v1.OrderService"));
        var after = Snapshot(Service("orders.v1.OrderService", Method("orders.v1.OrderService", "GetOrder")));

        Assert.Equal(0, await Diff(before, after, failOn: "breaking"));
    }

    [Fact]
    public async Task An_Added_Method_Does_Fail_A_Check_Set_To_Fail_On_Any()
    {
        // The stricter gate exists for APIs under a freeze.
        var before = Snapshot(Service("orders.v1.OrderService"));
        var after = Snapshot(Service("orders.v1.OrderService", Method("orders.v1.OrderService", "GetOrder")));

        Assert.Equal(1, await Diff(before, after, failOn: "any"));
    }

    [Fact]
    public async Task Fail_On_None_Is_The_Default_And_Never_Fails()
    {
        // The reporting-only mode a team starts with.
        var before = Snapshot(Service("orders.v1.OrderService", Method("orders.v1.OrderService", "GetOrder")));
        var after = Snapshot();

        Assert.Equal(0, await Diff(before, after));
    }

    [Fact]
    public void An_Unrecognised_Fail_On_Value_Never_Fails_The_Build()
    {
        // Same choice the lint gate makes: a typo in a CI file must not break
        // a build that was passing, because the failure would be attributed to
        // the API change rather than to the typo.
        var delta = BowireSchemaDiff.Compute(
            [Service("orders.v1.OrderService", Method("orders.v1.OrderService", "GetOrder"))],
            []);

        Assert.True(delta.HasBreakingChanges);
        Assert.Equal(0, DiffCommand.ExitCodeFor(delta, "brakeing"));
    }

    [Fact]
    public void The_Gate_Reads_Its_Level_Exactly_As_Written()
    {
        // Deliberately case-sensitive, unlike --format: the documented values
        // are lower-case, and this pins the current behaviour rather than
        // assuming it.
        var delta = BowireSchemaDiff.Compute(
            [Service("orders.v1.OrderService", Method("orders.v1.OrderService", "GetOrder"))],
            []);

        Assert.Equal(1, DiffCommand.ExitCodeFor(delta, "breaking"));
        Assert.Equal(0, DiffCommand.ExitCodeFor(delta, "Breaking"));
    }

    // ---- snapshot capture ----

    [Fact]
    public async Task Capturing_A_Snapshot_Without_A_Url_Prints_The_Usage()
        => Assert.Equal(2, await DiffCommand.RunSnapshotAsync(
            "", output: null, protocolId: null, Ct, _out, _err));

    [Fact]
    public async Task Capturing_Against_An_Unloaded_Protocol_Fails_Without_Writing_A_File()
    {
        // A snapshot file that exists but holds nothing would diff as "the
        // whole API was removed" on the next run.
        var path = OutputPath();

        var exit = await DiffCommand.RunSnapshotAsync(
            "https://api.example.com", path, protocolId: "not-a-real-protocol", Ct, _out, _err);

        Assert.Equal(1, exit);
        Assert.False(File.Exists(path));
    }
}
