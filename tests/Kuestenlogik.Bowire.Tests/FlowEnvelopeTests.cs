// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The workbench keeps a workspace's flows in one <c>flows.json</c>
/// envelope; the runner used to read one <c>FlowDefinition</c> per file.
/// </summary>
/// <remarks>
/// <para>
/// Pointed at a workspace the workbench had written, <c>bowire test
/// --workspace</c> read the envelope as a single flow, found no nodes,
/// and stopped with exit 2 — a workspace from the workbench was
/// unusable from the CLI, which is the half of the test pillar that runs
/// in CI.
/// </para>
/// <para>
/// The single-flow file is not going away: that is the shape the
/// workbench's own export writes (<c>.bwf</c>), so it stays the
/// interchange format while the envelope is the store.
/// </para>
/// </remarks>
public sealed class FlowEnvelopeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bowire-flow-envelope-" + Guid.NewGuid().ToString("N"));

    public FlowEnvelopeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string TwoFlows = """
    { "flows": [
      { "id": "flow_a", "name": "Alpha", "nodes": [
          { "id": "n1", "type": "request", "service": "S", "method": "M", "body": "{}" } ] },
      { "id": "flow_b", "name": "Beta", "nodes": [
          { "id": "n2", "type": "request", "service": "S", "method": "M", "body": "{}" } ] }
    ] }
    """;

    private const string OneFlow = """
    { "flows": [
      { "id": "only", "name": "Only", "nodes": [
          { "id": "n1", "type": "request", "service": "S", "method": "M", "body": "{}" } ] }
    ] }
    """;

    private const string SingleFile = """
    { "id": "exported", "name": "Exported", "nodes": [
      { "id": "n1", "type": "request", "service": "S", "method": "M", "body": "{}" } ] }
    """;

    [Fact]
    public void An_Envelope_Is_Recognised_As_Something_The_Flow_Runner_Handles()
    {
        // The discriminator decides which runner a file goes to. An
        // envelope that reads as "not a flow" would be handed to the
        // recording runner, which has even less to say about it.
        Assert.True(FlowTestRunner.LooksLikeFlow(TwoFlows));
        Assert.True(FlowTestRunner.LooksLikeFlow(SingleFile));
    }

    [Fact]
    public void A_Recording_Is_Still_Not_A_Flow()
    {
        Assert.False(FlowTestRunner.LooksLikeFlow("""{ "tests": [] }"""));
        Assert.False(FlowTestRunner.LooksLikeFlow("""{ "messages": [] }"""));
    }

    [Fact]
    public void The_Ids_Come_Back_In_Document_Order()
    {
        Assert.Equal(["flow_a", "flow_b"], FlowTestRunner.EnvelopeFlowIds(TwoFlows));
        // A single-flow file is not an envelope and has no ids to list.
        Assert.Empty(FlowTestRunner.EnvelopeFlowIds(SingleFile));
    }

    [Fact]
    public void A_Flow_Without_An_Id_Is_Addressable_By_Its_Index()
    {
        // The workbench always writes an id; a hand-edited document may
        // not, and a flow nothing can name is a flow nothing can run.
        var ids = FlowTestRunner.EnvelopeFlowIds("""
        { "flows": [ { "name": "Nameless", "nodes": [] }, { "id": "second", "nodes": [] } ] }
        """);

        Assert.Equal(["0", "second"], ids);
    }

    [Fact]
    public void A_Single_Flow_File_Loads_As_It_Always_Did()
    {
        var flow = FlowTestRunner.LoadFlow(SingleFile, flowId: null, out var error);

        Assert.Null(error);
        Assert.NotNull(flow);
        Assert.Equal("exported", flow.Id);
        Assert.Single(flow.Nodes);
    }

    [Fact]
    public void An_Envelope_Holding_One_Flow_Needs_No_Id()
    {
        var flow = FlowTestRunner.LoadFlow(OneFlow, flowId: null, out var error);

        Assert.Null(error);
        Assert.Equal("only", flow!.Id);
    }

    [Fact]
    public void Several_Flows_And_No_Id_Is_Refused_With_The_Choice_Spelled_Out()
    {
        var flow = FlowTestRunner.LoadFlow(TwoFlows, flowId: null, out var error);

        // Running the wrong flow of a workspace is worse than running
        // none, so this does not guess.
        Assert.Null(flow);
        Assert.NotNull(error);
        Assert.Contains("flow_a", error, StringComparison.Ordinal);
        Assert.Contains("flow_b", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Named_Flow_Is_The_One_That_Loads()
    {
        var flow = FlowTestRunner.LoadFlow(TwoFlows, flowId: "flow_b", out var error);

        Assert.Null(error);
        Assert.Equal("Beta", flow!.Name);
        Assert.Equal("n2", flow.Nodes[0].Id);
    }

    [Fact]
    public void An_Unknown_Id_Says_What_Is_On_Offer()
    {
        var flow = FlowTestRunner.LoadFlow(TwoFlows, flowId: "flow_z", out var error);

        Assert.Null(flow);
        Assert.Contains("flow_z", error!, StringComparison.Ordinal);
        Assert.Contains("flow_a, flow_b", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Workspace_The_Workbench_Wrote_Runs_Every_Flow_In_It()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_dir, "flows.json"), TwoFlows, TestContext.Current.CancellationToken);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var rc = await TestRunner.RunWorkspaceAsync(
            _dir, new TestCliOptions(), stdout, stderr);

        var text = stdout.ToString();
        // Both flows ran, each under its own name — the old behaviour was
        // one "Flow has no nodes" and exit 2.
        Assert.Contains("Alpha", text, StringComparison.Ordinal);
        Assert.Contains("Beta", text, StringComparison.Ordinal);
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task A_Workspace_Of_Single_Flow_Files_Behaves_As_Before()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_dir, "exported.json"), SingleFile, TestContext.Current.CancellationToken);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var rc = await TestRunner.RunWorkspaceAsync(
            _dir, new TestCliOptions(), stdout, stderr);

        Assert.Contains("Exported", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, rc);
    }
}
