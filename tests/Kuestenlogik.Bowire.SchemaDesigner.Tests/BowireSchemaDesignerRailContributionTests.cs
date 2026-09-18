// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.SchemaDesigner.Tests;

/// <summary>
/// What the rail tells the host about itself.
/// </summary>
/// <remarks>
/// Every value here is read by the workbench at startup and by nothing
/// else, so a typo in one is invisible until a rail fails to appear or
/// appears in the wrong place. The renderer keys in particular are a
/// contract with the JavaScript side: they are looked up by name, and a
/// mismatch is a blank pane rather than an error.
/// </remarks>
public sealed class BowireSchemaDesignerRailContributionTests
{
    private readonly BowireSchemaDesignerRailContribution _rail = new();

    [Fact]
    public void The_Rail_Identifies_Itself_The_Way_The_Host_Expects()
    {
        Assert.Equal("schema", _rail.Id);
        Assert.Equal("Schema Designer", _rail.DisplayName);
        Assert.Equal("graph", _rail.IconKey);
        Assert.Equal("work", _rail.Group);
    }

    [Fact]
    public void The_Renderer_Keys_Match_What_The_Sidebar_Kind_Announces()
    {
        // The host resolves both keys by name on the JavaScript side; a
        // mismatch shows up as an empty pane, not as an error.
        Assert.Equal("schema", _rail.SidebarKind);
        Assert.Equal("schemaSidebar", _rail.SidebarRendererKey);
        Assert.Equal("schemaMain", _rail.MainPaneRendererKey);
    }

    [Fact]
    public void It_Is_Off_Until_Asked_For_And_Needs_A_Workspace()
    {
        // A rail that drew itself into every fresh install would be the
        // wrong default for a tool most sessions never open; and it has
        // nothing to show without a workspace to read descriptors from.
        Assert.False(_rail.DefaultEnabled);
        Assert.True(_rail.RequiresWorkspace);
    }

    [Fact]
    public void It_Sorts_Among_The_Work_Rails_Rather_Than_At_Either_End()
    {
        // Not an assertion about the exact number, which is free to move:
        // about it being a deliberate position instead of the default 0
        // that would pin it to the top of the rail.
        Assert.InRange(_rail.SortIndex, 1, 1000);
    }
}
