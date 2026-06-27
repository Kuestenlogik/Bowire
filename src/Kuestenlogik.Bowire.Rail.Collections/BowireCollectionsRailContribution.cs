// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rail.Collections;

/// <summary>
/// Collections rail — standalone surface for the dedicated full-pane
/// editor (#306 Phase G; default-off behaviour established in #304).
/// </summary>
/// <remarks>
/// Default-off since #304: the Compose rail's side panel (#295) is now
/// the primary surface for managing collections + presets, and every
/// save flow (Discover "Add to", request-builder "Save to collection",
/// recording steps "Open in Compose") routes there. Operators who want
/// the dedicated full-pane editor back can re-enable it via Settings →
/// Rail modes; the <c>railMode === 'collections'</c> dispatch path stays
/// alive either way so embedded hosts that route there programmatically
/// keep working unchanged.
/// </remarks>
public sealed class BowireCollectionsRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "collections";
    /// <inheritdoc />
    public string DisplayName => "Collections";
    /// <inheritdoc />
    public string IconKey => "folder";
    /// <inheritdoc />
    public int SortIndex => 400;
    /// <inheritdoc />
    public string Group => "work";
    /// <inheritdoc />
    public string SidebarKind => "collections";
    /// <inheritdoc />
    // #304 — default-off. When the operator hasn't opted in via
    // Settings → Rail modes, the rail-strip icon, the workspace-tree
    // 'Collections' node, and the per-method 'C' pill suppress
    // themselves. Re-enabling restores all three surfaces.
    public bool DefaultEnabled => false;
    /// <inheritdoc />
    // Collections live inside the active workspace; clicking the rail
    // with no workspace would just paint an empty list and confuse the
    // first-run operator.
    public bool RequiresWorkspace => true;
}
