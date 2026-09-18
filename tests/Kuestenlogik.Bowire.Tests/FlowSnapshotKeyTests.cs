// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Which flow a snapshot baseline belongs to, when several flows live in one
/// file (#366).
/// </summary>
/// <remarks>
/// <para>
/// Baselines were filed under the flow <em>file's</em> stem. That was right
/// while a file held one flow: the export format still does, and a
/// <c>.bwf</c> beside its <c>__snapshots__/&lt;stem&gt;/</c> is the Jest
/// convention a reviewer already reads.
/// </para>
/// <para>
/// The workbench saves a workspace as one <c>flows.json</c> holding every
/// flow. Every one of them then has the same stem, so their baselines land
/// in one directory keyed only by step id — and two flows that both name a
/// step <c>n1</c> overwrite each other's baseline. Not a failure: the second
/// flow's response is quietly accepted as the first flow's truth.
/// </para>
/// </remarks>
public sealed class FlowSnapshotKeyTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bowire-snapkey-" + Guid.NewGuid().ToString("N"));

    public FlowSnapshotKeyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_Single_Flow_File_Keeps_Filing_Under_Its_Stem()
    {
        // The export format, and every baseline already checked in beside
        // one. Moving those would orphan them.
        var path = Path.Combine(_dir, "checkout.bwf");

        var dir = FlowTestRunner.SnapshotDirFor(path, flowId: null);

        Assert.Equal(Path.Combine(_dir, "__snapshots__", "checkout"), dir);
    }

    [Fact]
    public void Two_Flows_Of_One_Envelope_Do_Not_Share_A_Directory()
    {
        // The collision: same file, same stem, and step ids are only unique
        // within a flow.
        var path = Path.Combine(_dir, "flows.json");

        var first = FlowTestRunner.SnapshotDirFor(path, "flow_a");
        var second = FlowTestRunner.SnapshotDirFor(path, "flow_b");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_Flow_Of_An_Envelope_Is_Filed_Under_Its_Id()
    {
        // The id, because it is what survives: a flow keeps it when it is
        // renamed, reordered, or exported and re-imported, and it is what
        // `bowire test --workspace` already names each run by.
        var path = Path.Combine(_dir, "flows.json");

        var dir = FlowTestRunner.SnapshotDirFor(path, "flow_a");

        Assert.Equal(Path.Combine(_dir, "__snapshots__", "flow_a"), dir);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("../escape")]
    [InlineData("a:b")]
    public void An_Id_That_Is_A_Path_Cannot_Climb_Out(string flowId)
    {
        // Ids come from a file on disk, so they are not automatically a
        // single path segment. A baseline has to land under __snapshots__
        // whatever the id says.
        var path = Path.Combine(_dir, "flows.json");
        var root = Path.Combine(_dir, "__snapshots__");

        var dir = FlowTestRunner.SnapshotDirFor(path, flowId);

        Assert.StartsWith(root, Path.GetFullPath(dir), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_Flow_In_A_Workspace_Gets_Its_Own_Baseline()
    {
        // End to end through the workspace runner, which is where the two
        // flows actually meet.
        var flows = """
        { "flows": [
          { "id": "flow_a", "name": "Alpha", "nodes": [
              { "id": "n1", "type": "request", "service": "S", "method": "M", "body": "{}",
                "snapshot": { "mode": "exact" } } ] },
          { "id": "flow_b", "name": "Beta", "nodes": [
              { "id": "n1", "type": "request", "service": "S", "method": "M", "body": "{}",
                "snapshot": { "mode": "exact" } } ] }
        ] }
        """;
        await File.WriteAllTextAsync(
            Path.Combine(_dir, "flows.json"), flows, TestContext.Current.CancellationToken);

        using var stdout = new StringWriter();
        await TestRunner.RunWorkspaceAsync(_dir, new TestCliOptions(), stdout, TextWriter.Null);

        var snapshots = Path.Combine(_dir, "__snapshots__");
        Assert.True(Directory.Exists(Path.Combine(snapshots, "flow_a")),
            "flow_a has no baseline directory");
        Assert.True(Directory.Exists(Path.Combine(snapshots, "flow_b")),
            "flow_b has no baseline directory");
    }
}
