// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// <c>bowire test --workspace-id &lt;id&gt;</c> — running the suite of a
/// workspace the workbench saved, named the way the workbench names it
/// (#365).
/// </summary>
/// <remarks>
/// <para>
/// The path form needs the caller to know where a workspace's files ended
/// up, which depends on whether it is git-native and on the identity's slot.
/// The id is what the workbench shows on screen, so it is what a CI job
/// should be able to name.
/// </para>
/// <para>
/// The ticket recorded this as blocked on #97. It was not: #97 closed in
/// August and was about a migration dialog, not the file layout. What
/// actually stood in the way was the envelope — the workbench saves one
/// <c>flows.json</c> holding every flow, and the runner read one flow per
/// file — and that is fixed.
/// </para>
/// </remarks>
public sealed class WorkspaceIdTestRunTests : IDisposable
{
    private const string TwoFlows = """
    { "flows": [
      { "id": "flow_a", "name": "Alpha", "nodes": [
          { "id": "n1", "type": "request", "service": "S", "method": "M", "body": "{}" } ] },
      { "id": "flow_b", "name": "Beta", "nodes": [
          { "id": "n2", "type": "request", "service": "S", "method": "M", "body": "{}" } ] }
    ] }
    """;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-wsid-" + Guid.NewGuid().ToString("N"));

    /// <summary>This class's storage, for as long as it runs.</summary>
    private readonly IDisposable _userScope;

    public WorkspaceIdTestRunTests()
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

    // ---- resolving the id ----

    [Fact]
    public async Task A_Workspace_In_The_Identity_Slot_Runs_Every_Flow_In_It()
    {
        WriteInventory("""{"workspaces":[{"id":"harbor","name":"Harbour ops"}]}""");
        WriteFlows(workspaceId: "harbor", storageRoot: null, TwoFlows);

        var (rc, stdout, _) = await RunAsync("harbor");

        // Both flows, each under its own name — the envelope is expanded,
        // not handed over as though it were one flow.
        Assert.Contains("Alpha", stdout, StringComparison.Ordinal);
        Assert.Contains("Beta", stdout, StringComparison.Ordinal);
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task A_Git_Native_Workspace_Is_Read_From_Its_Checkout()
    {
        // The reason a directory scan is not enough: a git-native
        // workspace's flows live in the repository, and only the inventory
        // knows where that is.
        var checkout = Path.Combine(_root, "checkouts", "harbor");
        Directory.CreateDirectory(checkout);
        WriteInventory($$"""
        {"workspaces":[{"id":"harbor","name":"Harbour ops",
          "storageRoot":{{System.Text.Json.JsonSerializer.Serialize(checkout)}}}]}
        """);
        WriteFlows(workspaceId: "harbor", storageRoot: checkout, TwoFlows);

        var (rc, stdout, _) = await RunAsync("harbor");

        Assert.Contains("Alpha", stdout, StringComparison.Ordinal);
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task The_Header_Names_The_Workspace_And_The_File()
    {
        // A run that says only "workspace" leaves the operator unable to
        // tell which one CI just reported on.
        WriteInventory("""{"workspaces":[{"id":"harbor","name":"Harbour ops"}]}""");
        WriteFlows(workspaceId: "harbor", storageRoot: null, TwoFlows);

        var (_, stdout, _) = await RunAsync("harbor");

        Assert.Contains("Harbour ops", stdout, StringComparison.Ordinal);
        Assert.Contains("harbor", stdout, StringComparison.Ordinal);
        Assert.Contains("flows.json", stdout, StringComparison.Ordinal);
    }

    // ---- what it refuses, and how ----

    [Fact]
    public async Task An_Unknown_Id_Is_Told_Which_Ids_Exist()
    {
        // An id is easy to mistype and there is no other way to see the list
        // from inside a CI log.
        WriteInventory("""
        {"workspaces":[{"id":"harbor","name":"Harbour"},{"id":"berths","name":"Berths"}]}
        """);

        var (rc, _, stderr) = await RunAsync("harbour");

        Assert.Equal(2, rc);
        Assert.Contains("harbour", stderr, StringComparison.Ordinal);
        Assert.Contains("harbor", stderr, StringComparison.Ordinal);
        Assert.Contains("berths", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Id_Is_Matched_Exactly()
    {
        // Ordinal, the same rule the store uses: two ids differing only in
        // case are two workspaces, and running the other one is worse than
        // running none.
        WriteInventory("""{"workspaces":[{"id":"harbor","name":"Harbour"}]}""");
        WriteFlows(workspaceId: "harbor", storageRoot: null, TwoFlows);

        var (rc, _, _) = await RunAsync("Harbor");

        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task An_Install_With_No_Workspaces_Says_What_To_Do_Instead()
    {
        var (rc, _, stderr) = await RunAsync("harbor");

        Assert.Equal(2, rc);
        Assert.Contains("--workspace", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Workspace_That_Has_Saved_No_Flows_Yet_Says_So()
    {
        // Distinct from an unknown id: the workspace is real, it is just
        // empty, and the two need different things done about them.
        WriteInventory("""{"workspaces":[{"id":"harbor","name":"Harbour"}]}""");

        var (rc, _, stderr) = await RunAsync("harbor");

        Assert.Equal(2, rc);
        Assert.Contains("harbor", stderr, StringComparison.Ordinal);
        Assert.Contains("no flows", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_Corrupt_Inventory_Does_Not_Crash_The_Run()
    {
        // The file is machine-written, but it is on the operator's disk and
        // a half-written one is what a crash during save leaves behind.
        WriteInventory("{ this is not json");

        var (rc, _, stderr) = await RunAsync("harbor");

        Assert.Equal(2, rc);
        Assert.False(string.IsNullOrWhiteSpace(stderr));
    }

    // ---- the inventory reader ----

    [Fact]
    public void An_Entry_Without_An_Id_Is_Not_Listed()
    {
        // The id is what a caller names; an entry without one cannot be
        // asked for.
        WriteInventory("""
        {"workspaces":[{"name":"nameless"},{"id":"harbor","name":"Harbour"}]}
        """);

        var only = Assert.Single(WorkbenchWorkspaces.All());
        Assert.Equal("harbor", only.Id);
    }

    [Fact]
    public void A_Workspace_Without_A_Name_Answers_To_Its_Id()
    {
        WriteInventory("""{"workspaces":[{"id":"harbor"}]}""");

        Assert.Equal("harbor", Assert.Single(WorkbenchWorkspaces.All()).Name);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"workspaces":{"id":"harbor"}}""")]
    [InlineData("""{"other":[]}""")]
    [InlineData("not json at all")]
    public void An_Inventory_That_Is_Not_The_Expected_Document_Reads_As_Empty(string raw)
    {
        WriteInventory(raw);

        Assert.Empty(WorkbenchWorkspaces.All());
    }

    // ---- harness ----

    private void WriteInventory(string json)
        => File.WriteAllText(Path.Combine(_root, "workspaces.json"), json);

    private static void WriteFlows(string workspaceId, string? storageRoot, string json)
    {
        var path = BowireUserContext.GetWorkspacePath(workspaceId, storageRoot, "flows.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static async Task<(int Rc, string Stdout, string Stderr)> RunAsync(string workspaceId)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var rc = await TestRunner.RunWorkspaceIdAsync(
            workspaceId, new TestCliOptions(), stdout, stderr);

        return (rc, stdout.ToString(), stderr.ToString());
    }
}
