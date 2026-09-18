// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Flows;
using Kuestenlogik.Bowire.Flows.Expectations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Flows.Tests;

/// <summary>
/// <c>/api/flows/snapshot/compare</c> and <c>/approve</c> — seeing snapshot
/// drift where it happened, and accepting it there (#171).
/// </summary>
/// <remarks>
/// Re-baselining was a CLI flag, so the person who could see the drift was
/// not the one who could act on it. What matters here is that an approval
/// lands in the file the CLI later compares against: an approve that writes
/// somewhere else leaves CI comparing against the old baseline, and the
/// workbench saying it was accepted.
/// </remarks>
public sealed class BowireFlowSnapshotEndpointsTests : IDisposable
{
    private const string WorkspaceId = "harbor";
    private const string FlowId = "flow_a";
    private const string StepId = "n1";

    private static readonly string[] IgnoreUpdatedAt = ["$.updatedAt"];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-snapep-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _userScope;

    public BowireFlowSnapshotEndpointsTests()
    {
        Directory.CreateDirectory(_root);
        _userScope = BowireUserContext.Enter(new DefaultBowireUserStore(_root));
    }

    public void Dispose()
    {
        _userScope.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- compare ----

    [Fact]
    public async Task A_Step_With_No_Baseline_Says_So_Rather_Than_No_Differences()
    {
        // The two look identical on screen and mean opposite things: one is
        // a snapshot that holds, the other has never guarded anything.
        var body = await PostAsync("compare", new { workspaceId = WorkspaceId, flowId = FlowId, stepId = StepId, actual = "{}" });

        Assert.False(body.GetProperty("captured").GetBoolean());
        Assert.Empty(body.GetProperty("diffs").EnumerateArray());
    }

    [Fact]
    public async Task A_Matching_Response_Reports_No_Drift()
    {
        await SeedBaselineAsync("""{"id":42,"name":"Ada"}""");

        var body = await PostAsync("compare", new
        {
            workspaceId = WorkspaceId, flowId = FlowId, stepId = StepId,
            actual = """{"id":42,"name":"Ada"}""",
        });

        Assert.True(body.GetProperty("captured").GetBoolean());
        Assert.Empty(body.GetProperty("diffs").EnumerateArray());
    }

    [Fact]
    public async Task Drift_Comes_Back_As_Lines_A_Person_Can_Read()
    {
        await SeedBaselineAsync("""{"id":42,"name":"Ada"}""");

        var body = await PostAsync("compare", new
        {
            workspaceId = WorkspaceId, flowId = FlowId, stepId = StepId,
            actual = """{"id":42,"name":"Grace"}""",
        });

        var diffs = body.GetProperty("diffs").EnumerateArray().Select(d => d.GetString()!).ToList();
        Assert.NotEmpty(diffs);
        // The field that moved has to be named, or the diff view is just a
        // red light.
        Assert.Contains(diffs, d => d.Contains("name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_Baseline_Travels_Back_So_The_View_Can_Show_Both_Sides()
    {
        await SeedBaselineAsync("""{"id":42}""");

        var body = await PostAsync("compare", new
        {
            workspaceId = WorkspaceId, flowId = FlowId, stepId = StepId, actual = """{"id":43}""",
        });

        Assert.Equal("""{"id":42}""", body.GetProperty("baseline").GetString());
    }

    [Fact]
    public async Task An_Ignored_Path_Is_Not_Drift()
    {
        // The reason the mode and the ignore list travel: a timestamp that
        // moves every run would otherwise make the snapshot useless.
        await SeedBaselineAsync("""{"id":42,"updatedAt":"2026-01-01"}""");

        var body = await PostAsync("compare", new
        {
            workspaceId = WorkspaceId, flowId = FlowId, stepId = StepId,
            actual = """{"id":42,"updatedAt":"2026-09-18"}""",
            ignore = IgnoreUpdatedAt,
        });

        Assert.Empty(body.GetProperty("diffs").EnumerateArray());
    }

    // ---- approve ----

    [Fact]
    public async Task Approving_Writes_The_File_The_Runner_Reads()
    {
        // The whole point. Resolved independently here, through the store
        // the CLI uses, so this fails if the endpoint works the path out its
        // own way.
        var body = await PostAsync("approve", new
        {
            workspaceId = WorkspaceId, flowId = FlowId, stepId = StepId,
            actual = """{"id":42,"name":"Grace"}""",
        });

        Assert.True(body.GetProperty("approved").GetBoolean());

        var expected = FlowSnapshotStore.FileFor(
            FlowSnapshotStore.DirectoryForWorkspace(WorkspaceId, null, FlowId), StepId);
        Assert.True(File.Exists(expected), expected);
        Assert.Equal("""{"id":42,"name":"Grace"}""",
            await File.ReadAllTextAsync(expected, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Approving_Says_Where_The_File_Went()
    {
        // "Approved" without a location is a claim the person cannot check,
        // and this one ends up in their commit.
        var body = await PostAsync("approve", new
        {
            workspaceId = WorkspaceId, flowId = FlowId, stepId = StepId, actual = "{}",
        });

        var file = body.GetProperty("file").GetString()!;
        Assert.Contains(FlowSnapshotStore.DirectoryName, file, StringComparison.Ordinal);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task An_Approved_Response_Then_Compares_Clean()
    {
        await SeedBaselineAsync("""{"id":42,"name":"Ada"}""");
        await PostAsync("approve", new
        {
            workspaceId = WorkspaceId, flowId = FlowId, stepId = StepId,
            actual = """{"id":42,"name":"Grace"}""",
        });

        var body = await PostAsync("compare", new
        {
            workspaceId = WorkspaceId, flowId = FlowId, stepId = StepId,
            actual = """{"id":42,"name":"Grace"}""",
        });

        Assert.Empty(body.GetProperty("diffs").EnumerateArray());
    }

    [Fact]
    public async Task Two_Flows_Do_Not_Approve_Over_Each_Other()
    {
        // Step ids are only unique within a flow, and the workbench saves
        // every flow of a workspace in one file.
        await PostAsync("approve", new
        {
            workspaceId = WorkspaceId, flowId = "flow_a", stepId = StepId, actual = """{"who":"a"}""",
        });
        await PostAsync("approve", new
        {
            workspaceId = WorkspaceId, flowId = "flow_b", stepId = StepId, actual = """{"who":"b"}""",
        });

        var body = await PostAsync("compare", new
        {
            workspaceId = WorkspaceId, flowId = "flow_a", stepId = StepId, actual = """{"who":"a"}""",
        });

        Assert.Empty(body.GetProperty("diffs").EnumerateArray());
    }

    // ---- what is refused ----

    [Theory]
    [InlineData("flowId")]
    [InlineData("stepId")]
    [InlineData("workspaceId")]
    public async Task A_Missing_Name_Is_Refused_With_The_Reason(string missing)
    {
        var payload = new Dictionary<string, object?>
        {
            ["workspaceId"] = WorkspaceId,
            ["flowId"] = FlowId,
            ["stepId"] = StepId,
            ["actual"] = "{}",
        };
        payload[missing] = null;

        var (status, raw) = await PostRawAsync("approve", payload);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(missing, raw, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    public async Task A_Workspace_Id_That_Is_A_Path_Is_Refused(string workspaceId)
    {
        // This route writes a file. The pair goes through the same check the
        // core endpoints apply rather than a second copy of it.
        var (status, raw) = await PostRawAsync("approve", new Dictionary<string, object?>
        {
            ["workspaceId"] = workspaceId,
            ["flowId"] = FlowId,
            ["stepId"] = StepId,
            ["actual"] = "{}",
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("workspaceId", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Storage_Root_That_Does_Not_Exist_Is_Refused()
    {
        // A root is a directory the operator already pointed Bowire at.
        // Creating one because a request named it is the difference between
        // writing where somebody chose and writing where a request invented.
        var (status, raw) = await PostRawAsync("approve", new Dictionary<string, object?>
        {
            ["workspaceId"] = WorkspaceId,
            ["storageRoot"] = Path.Combine(_root, "never-created"),
            ["flowId"] = FlowId,
            ["stepId"] = StepId,
            ["actual"] = "{}",
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("storageRoot", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Body_That_Is_Not_Json_Is_Refused()
    {
        using var host = await BuildHost();
        using var client = host.GetTestClient();
        using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(
            new Uri("/api/flows/snapshot/compare", UriKind.Relative), content,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- harness ----

    private static async Task SeedBaselineAsync(string baseline)
    {
        var dir = FlowSnapshotStore.DirectoryForWorkspace(WorkspaceId, null, FlowId);
        await FlowSnapshotStore.WriteAsync(dir, StepId, baseline, TestContext.Current.CancellationToken);
    }

    private static async Task<IHost> BuildHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .ConfigureServices(s => s.AddRouting())
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e =>
                           new BowireFlowSnapshotEndpoints().MapEndpoints(e, string.Empty));
                   });
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        // The store scope this class opened rides an AsyncLocal, and
        // TestServer drops the caller's execution context unless told not
        // to. Without this the handler resolves against the host's store —
        // the real ~/.bowire — and writes a baseline there.
        host.GetTestServer().PreserveExecutionContext = true;
        return host;
    }

    private static async Task<JsonElement> PostAsync(string route, object payload)
    {
        using var host = await BuildHost();
        using var client = host.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            new Uri($"/api/flows/snapshot/{route}", UriKind.Relative), payload,
            TestContext.Current.CancellationToken);

        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {raw}");
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static async Task<(HttpStatusCode Status, string Raw)> PostRawAsync(
        string route, Dictionary<string, object?> payload)
    {
        using var host = await BuildHost();
        using var client = host.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            new Uri($"/api/flows/snapshot/{route}", UriKind.Relative), payload,
            TestContext.Current.CancellationToken);

        return (response.StatusCode,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
