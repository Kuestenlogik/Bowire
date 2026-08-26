// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// Which workspace <c>bowire workspace migrate --to-project</c> picks when the
/// operator does not name one.
/// </summary>
/// <remarks>
/// <para>
/// The mapping itself is covered in
/// <see cref="WorkspaceMigrateToProjectTests"/> against a temp tree. This is
/// the layer above it: the one that reads <c>~/.bowire/workspaces</c> and
/// decides. Guessing wrong here migrates somebody else's workspace into the
/// repository the operator is standing in — and the manifest that lands looks
/// perfectly valid, so nothing downstream catches it.
/// </para>
/// <para>
/// The user store is redirected at a temp home for the duration, which is also
/// why the class runs in the serialised <c>BowireUserContext</c> collection.
/// </para>
/// </remarks>
[Collection("BowireUserContext")]
public sealed class WorkspaceMigrateResolutionTests : IDisposable
{
    private const int Ok = 0;
    private const int Usage = 64;
    private const int NoInput = 66;

    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "bowire-ws-resolve-" + Guid.NewGuid().ToString("N"));
    private readonly IBowireUserStore _previous = BowireUserContext.Current;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public WorkspaceMigrateResolutionTests()
    {
        Directory.CreateDirectory(_home);
        BowireUserContext.Current = new DefaultBowireUserStore(_home);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _previous;
        _out.Dispose();
        _err.Dispose();
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Materialise a workspace under the temp home's workspaces root.</summary>
    private string AddWorkspace(string id)
    {
        var root = Path.Combine(_home, "workspaces", id);
        Directory.CreateDirectory(Path.Combine(root, "collections"));
        File.WriteAllText(Path.Combine(root, "workspace.json"),
            $$"""{"workspaceFormatVersion":1,"id":"{{id}}","name":"{{id}}"}""");
        return root;
    }

    private string OutDir()
    {
        var dir = Path.Combine(_home, "out-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private Task<int> Migrate(string? workspaceId = null, bool force = false)
        => WorkspaceCommand.RunMigrateToProjectAsync(workspaceId, OutDir(), force, _out, _err, Ct);

    [Fact]
    public async Task With_No_Workspaces_Root_At_All_It_Says_Where_It_Looked()
    {
        // Nothing has ever been saved on this machine. Naming the path is what
        // separates "you have no workspaces" from "I looked in the wrong
        // place", and only the operator can tell those apart.
        var exit = await Migrate();

        Assert.Equal(NoInput, exit);
        Assert.Contains("workspaces", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains(_home, _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Empty_Workspaces_Root_Is_Reported_Rather_Than_Migrating_Nothing()
    {
        // The directory exists but holds nothing — a fresh install that has
        // opened the workbench once. Writing an empty manifest here would be
        // worse than refusing: it looks like a successful migration.
        Directory.CreateDirectory(Path.Combine(_home, "workspaces"));

        var exit = await Migrate();

        Assert.Equal(NoInput, exit);
        Assert.Contains("no workspaces", _err.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_Single_Workspace_Is_Picked_Without_Being_Named()
    {
        // The common case, and the reason --workspace is optional: with one
        // workspace there is nothing to choose.
        AddWorkspace("ws_only");

        var exit = await Migrate();

        Assert.Equal(Ok, exit);
        Assert.Contains("ws_only", _out.ToString() + _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Several_Workspaces_Make_It_Ask_And_List_The_Choices()
    {
        // Picking one silently is the failure this prevents: the manifest that
        // lands is valid, so the operator finds out only when the wrong
        // collections show up in their repository.
        AddWorkspace("ws_alpha");
        AddWorkspace("ws_beta");

        var exit = await Migrate();

        Assert.Equal(Usage, exit);
        var err = _err.ToString();
        Assert.Contains("--workspace", err, StringComparison.Ordinal);
        Assert.Contains("ws_alpha", err, StringComparison.Ordinal);
        Assert.Contains("ws_beta", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Naming_One_Of_Several_Migrates_That_One()
    {
        AddWorkspace("ws_alpha");
        AddWorkspace("ws_beta");

        var exit = await Migrate(workspaceId: "ws_beta");

        Assert.Equal(Ok, exit);
        Assert.Contains("ws_beta", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Surrounding_Whitespace_On_The_Id_Is_Trimmed()
    {
        // Ids get pasted from the workbench, which is where the stray space
        // comes from. Failing on it would be a puzzle with no clue in it.
        AddWorkspace("ws_only");

        Assert.Equal(Ok, await Migrate(workspaceId: "  ws_only  "));
    }

    [Fact]
    public async Task An_Id_Nothing_Answers_To_Names_The_Path_It_Tried()
    {
        // A typo in a pasted id. The message carries the resolved directory so
        // the operator can see what was actually looked for.
        AddWorkspace("ws_only");

        var exit = await Migrate(workspaceId: "ws_typo");

        Assert.Equal(NoInput, exit);
        Assert.Contains("ws_typo", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Blank_Id_Falls_Back_To_The_Automatic_Pick()
    {
        // `--workspace ""` from a shell script must behave like no flag at
        // all, rather than looking up a workspace with an empty name.
        AddWorkspace("ws_only");

        Assert.Equal(Ok, await Migrate(workspaceId: "   "));
    }
}
