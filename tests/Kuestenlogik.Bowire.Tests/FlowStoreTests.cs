// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Flows on disk, where every other artifact already was (#641).
/// </summary>
/// <remarks>
/// <para>
/// Flows were the last thing the workbench kept only in the browser. The four
/// symptoms that came of it each looked like a separate defect — two MCP
/// resources reading a file nothing wrote, <c>bowire test</c> blind to
/// anything built in the workbench, flows absent from a git-native workspace,
/// flows outside the per-identity slot — and all four are this one gap.
/// </para>
/// <para>
/// The store is deliberately the collection store's shape, so these are
/// deliberately the collection store's tests: the same three questions that
/// mattered there (does a workspace see its own, does an unsaved workspace
/// inherit somebody else's, does a corrupt file take the workbench down) are
/// the ones that matter here.
/// </para>
/// </remarks>
[Collection("BowireUserContext")]
public sealed class FlowStoreTests : IDisposable
{
    private const string TwoFlows =
        """{"flows":[{"id":"flow_a","name":"Login"},{"id":"flow_b","name":"Checkout"}]}""";

    private readonly IBowireUserStore _previousUsers = BowireUserContext.Current;
    private readonly string _originalPath;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-flows-" + Guid.NewGuid().ToString("N"));

    public FlowStoreTests()
    {
        Directory.CreateDirectory(_root);
        _originalPath = FlowStore.StorePath;
        BowireUserContext.Current = new DefaultBowireUserStore(_root);
        FlowStore.StorePath = Path.Combine(_root, "flows.json");
    }

    public void Dispose()
    {
        FlowStore.StorePath = _originalPath;
        BowireUserContext.Current = _previousUsers;
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void AWorkspacesFlowsRoundTrip()
    {
        FlowStore.Save(TwoFlows, "team-a");

        Assert.Equal(TwoFlows, FlowStore.Load("team-a"));
    }

    [Fact]
    public void OneWorkspacesFlowsAreNotAnothers()
    {
        // The bleed the collection store had to learn about the hard way
        // (#612): every workspace read and wrote one file, so whoever saved
        // last handed their state to everyone else.
        FlowStore.Save(TwoFlows, "team-a");

        Assert.Equal("""{"flows":[]}""", FlowStore.Load("team-b"));
    }

    [Fact]
    public void AWorkspaceThatNeverSavedDoesNotInheritTheLegacyFile()
    {
        // Handing the global file to the first workspace that happens to look
        // would land on top of whatever a template just seeded.
        FlowStore.Save(TwoFlows);

        Assert.Equal("""{"flows":[]}""", FlowStore.Load("team-a"));
    }

    [Fact]
    public void AGitNativeWorkspaceKeepsItsFlowsInTheCheckout()
    {
        // The point of storageRoot: the flow is a file in the repository, so
        // it is reviewable in a diff and arrives with a clone. That is what
        // makes a shared regression path shared.
        var checkout = Path.Combine(_root, "checkout");
        Directory.CreateDirectory(checkout);

        FlowStore.Save(TwoFlows, "team-a", checkout);

        Assert.True(File.Exists(Path.Combine(checkout, "flows.json")));
        Assert.Equal(TwoFlows, FlowStore.Load("team-a", checkout));
    }

    [Fact]
    public void ACorruptFileReadsAsEmptyRatherThanThrowing()
    {
        // A workbench that will not open because one file is malformed is a
        // worse outcome than one that opens with a flow list to rebuild.
        var path = Path.Combine(_root, "workspaces", "team-a", "flows.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        Assert.Equal("""{"flows":[]}""", FlowStore.Load("team-a"));
    }

    [Fact]
    public void AMalformedSaveIsRefusedBeforeItReachesDisk()
    {
        // The caller's bug must not become a file the next load has to
        // recover from.
        FlowStore.Save(TwoFlows, "team-a");

        Assert.ThrowsAny<Exception>(() => FlowStore.Save("{ not json", "team-a"));
        Assert.Equal(TwoFlows, FlowStore.Load("team-a"));
    }

    [Fact]
    public void AnEmptyPayloadIsRefused()
    {
        Assert.Throws<ArgumentException>(() => FlowStore.Save("  ", "team-a"));
    }
}
