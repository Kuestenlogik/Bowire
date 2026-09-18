// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mcp;
using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.Mcp.Tests;

/// <summary>
/// Where the MCP surface reads Bowire's own configuration from (#616, #642).
/// </summary>
/// <remarks>
/// This type exists because four call sites each rebuilt the same path and
/// drifted apart, so the MCP tools answered from the old place while
/// reporting success. Reached only through the tools and resources until
/// now, which covered the paths those happen to take and left the
/// inventory's malformed shapes and the workspace resolution untested —
/// the two things an agent's request actually turns on.
/// </remarks>
[Collection(nameof(BowireConfigFixture))]
public sealed class McpPathsTests : IDisposable
{
    private readonly string _home;
    private readonly string _bowire;
    private readonly string? _previous;

    public McpPathsTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "bowire-mcppaths-" + Guid.NewGuid().ToString("N")[..8]);
        _bowire = Path.Combine(_home, ".bowire");
        Directory.CreateDirectory(_bowire);

        _previous = McpPaths.HomeDirOverride;
        McpPaths.HomeDirOverride = _home;
    }

    public void Dispose()
    {
        McpPaths.HomeDirOverride = _previous;
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void WriteInventory(string json)
        => File.WriteAllText(Path.Combine(_bowire, "workspaces.json"), json);

    // ---- Config ----

    [Fact]
    public void A_Config_File_Sits_Under_The_Overridden_Home()
    {
        // The override is a home directory, not a storage root, so the
        // .bowire segment is this method's to add.
        Assert.Equal(
            Path.Combine(_home, ".bowire", "environments.json"),
            McpPaths.Config("environments.json"));
    }

    [Fact]
    public void Without_An_Override_The_Resolver_Decides()
    {
        // The whole point of #616: no second copy of the layout rule, so a
        // storage root that moves takes the MCP surface with it.
        McpPaths.HomeDirOverride = null;

        var path = McpPaths.Config("environments.json");

        Assert.Equal(
            BowirePaths.Resolve(BowireStorageScope.Data, "environments.json"),
            path);
    }

    // ---- Workspaces ----

    [Fact]
    public void No_Inventory_Means_No_Workspaces()
    {
        Assert.Empty(McpPaths.Workspaces());
    }

    [Fact]
    public void The_Inventory_Is_Read_Into_Entries()
    {
        WriteInventory("""
        {"workspaces":[
          {"id":"harbor","name":"Harbour ops","storageRoot":"C:/checkouts/harbor"},
          {"id":"berths","name":"Berths"}
        ]}
        """);

        var workspaces = McpPaths.Workspaces();

        Assert.Equal(2, workspaces.Count);
        Assert.Equal("harbor", workspaces[0].Id);
        Assert.Equal("Harbour ops", workspaces[0].Name);
        Assert.Equal("C:/checkouts/harbor", workspaces[0].StorageRoot);
        // Not git-native: no checkout to read from.
        Assert.Null(workspaces[1].StorageRoot);
    }

    [Fact]
    public void A_Workspace_Without_A_Name_Answers_To_Its_Id()
    {
        // An agent is shown this list to pick from; a blank line in it is
        // worse than a technical id.
        WriteInventory("""{"workspaces":[{"id":"harbor"}]}""");

        Assert.Equal("harbor", Assert.Single(McpPaths.Workspaces()).Name);
    }

    [Fact]
    public void An_Entry_With_No_Id_Is_Skipped_Rather_Than_Listed()
    {
        // The id is what a resource URI names, so an entry without one
        // cannot be asked for and has no business in the list.
        WriteInventory("""
        {"workspaces":[{"name":"nameless"},{"id":"","name":"blank"},{"id":"harbor"}]}
        """);

        Assert.Equal("harbor", Assert.Single(McpPaths.Workspaces()).Id);
    }

    [Theory]
    // Not JSON at all.
    [InlineData("{ not json")]
    // JSON, but not the document this reads.
    [InlineData("[]")]
    [InlineData("\"harbor\"")]
    // The right shape with the wrong contents.
    [InlineData("""{"other":[]}""")]
    [InlineData("""{"workspaces":{"id":"harbor"}}""")]
    [InlineData("""{"workspaces":["harbor",42]}""")]
    [InlineData("   ")]
    public void An_Unreadable_Inventory_Reads_As_No_Workspaces(string raw)
    {
        // Deliberate: the workspace-less resources still answer rather than
        // the whole MCP surface failing over one file an agent never asked
        // about.
        WriteInventory(raw);

        Assert.Empty(McpPaths.Workspaces());
    }

    // ---- WorkspaceConfig ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void No_Workspace_Named_Means_No_Path(string? workspaceId)
    {
        WriteInventory("""{"workspaces":[{"id":"harbor"}]}""");

        Assert.Null(McpPaths.WorkspaceConfig(workspaceId!, "flows.json"));
    }

    [Fact]
    public void An_Unknown_Id_Is_Null_Rather_Than_An_Empty_Document()
    {
        // The caller turns this into a message naming the index. An agent
        // handed "no data" cannot tell a wrong id from an empty workspace,
        // and will report the workspace as empty.
        WriteInventory("""{"workspaces":[{"id":"harbor"}]}""");

        Assert.Null(McpPaths.WorkspaceConfig("berths", "flows.json"));
    }

    [Fact]
    public void An_Id_Is_Matched_Exactly()
    {
        // Ordinal, because a workspace id is a key rather than a label —
        // two ids differing only in case are two workspaces.
        WriteInventory("""{"workspaces":[{"id":"harbor"}]}""");

        Assert.Null(McpPaths.WorkspaceConfig("Harbor", "flows.json"));
        Assert.NotNull(McpPaths.WorkspaceConfig("harbor", "flows.json"));
    }

    [Fact]
    public void A_Git_Native_Workspace_Resolves_Into_Its_Checkout()
    {
        // The reason this is worth the indirection: an agent reads what a
        // clone carries, not a private copy beside it.
        var checkout = Path.Combine(_home, "checkouts", "harbor");
        WriteInventory($$"""
        {"workspaces":[{"id":"harbor","storageRoot":{{System.Text.Json.JsonSerializer.Serialize(checkout)}}}]}
        """);

        var path = McpPaths.WorkspaceConfig("harbor", "flows.json");

        Assert.Equal(Path.Combine(checkout, "flows.json"), path);
    }

    [Fact]
    public void A_Plain_Workspace_Resolves_Under_The_Overridden_Home()
    {
        WriteInventory("""{"workspaces":[{"id":"harbor"}]}""");

        var path = McpPaths.WorkspaceConfig("harbor", "flows.json");

        Assert.Equal(Path.Combine(_home, ".bowire", "workspaces", "harbor", "flows.json"), path);
    }

    // ---- Plugins ----

    [Fact]
    public void The_Plugin_Directory_Follows_The_Override()
    {
        // The override wins here so a suite stays off the developer's real
        // ~/.bowire — which is where their actual plugins are installed.
        Assert.Equal(Path.Combine(_home, ".bowire", "plugins"), McpPaths.Plugins());
    }

    [Fact]
    public void Without_An_Override_The_Plugin_Root_Is_The_One_The_Host_Loads_From()
    {
        // Not the raw resolver (#549): a host started with --plugin-dir
        // loads from somewhere else, and bowire.plugins has to report the
        // directory the plugins actually came from.
        McpPaths.HomeDirOverride = null;

        Assert.Equal(Kuestenlogik.Bowire.Plugins.BowirePluginRoot.Current, McpPaths.Plugins());
    }
}
