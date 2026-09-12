// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.SchemaDesigner;

/// <summary>
/// Schema Designer rail contribution (#247).
/// </summary>
/// <remarks>
/// <para>
/// Sits next to Discover rather than in a group of its own. Discover answers
/// "what does this URL expose"; the Designer answers "what shape is it" — a
/// different question about the same subject, so it belongs beside it in the
/// <c>work</c> group and not in the separate <c>dev</c> group #247 sketched.
/// A group with one member is a divider, not a grouping.
/// </para>
/// <para>
/// Ships <see cref="DefaultEnabled"/> = <c>false</c>, which is #249's Phase 2
/// in full: the toggle mechanic (#248, #304) already reads this flag, so an
/// existing operator finds the Designer unticked under Settings → Rail modes
/// instead of finding a new icon on their strip.
/// </para>
/// </remarks>
public sealed class BowireSchemaDesignerRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "schema";

    /// <inheritdoc />
    public string DisplayName => "Schema Designer";

    /// <inheritdoc />
    public string IconKey => "graph";

    /// <inheritdoc />
    // Between Discover (200) and Compose (300) — you discover a schema,
    // understand it, then compose against it.
    public int SortIndex => 250;

    /// <inheritdoc />
    public string Group => "work";

    /// <inheritdoc />
    public string SidebarKind => "schema";

    /// <inheritdoc />
    public string? SidebarRendererKey => "schemaSidebar";

    /// <inheritdoc />
    public string? MainPaneRendererKey => "schemaMain";

    /// <inheritdoc />
    // Heavyweight and not universally wanted — the first rail to use the
    // default-off flag (#249).
    public bool DefaultEnabled => false;

    /// <inheritdoc />
    // The graph is built from whatever the active workspace discovered.
    // Without a workspace there is no schema to draw.
    public bool RequiresWorkspace => true;
}
