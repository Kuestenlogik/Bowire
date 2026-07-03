// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Compose;

/// <summary>
/// Compose rail contribution — home for the ad-hoc Request Builder (#306 Phase G).
/// </summary>
public sealed class BowireComposeRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "compose";
    /// <inheritdoc />
    public string DisplayName => "Compose";
    /// <inheritdoc />
    public string IconKey => "compose";
    /// <inheritdoc />
    public int SortIndex => 300;
    /// <inheritdoc />
    public string Group => "work";
    /// <inheritdoc />
    // The Library (Collections + Presets) lives in the standard
    // workbench sidebar slot — same chrome, splitter, edge-toggle,
    // and hover-intent as Discover / Recordings / Workspaces /
    // Mocks. Operator: 'compose library drawer sollte stattdessen
    // als sidebar analog zu discover und recordings sein. momentan
    // wirkt er eher wie ein fremdkörper. außerdem kann dann auch
    // der splitter analog wie bei discover usw. verwendet werden.'
    // Dispatched by the case 'library' arm in render-sidebar.js's
    // renderSidebar() switch, which delegates to
    // window.renderComposeLibrarySidebar() exposed by compose-rail.js.
    public string SidebarKind => "library";

    // #306 / #314 — Compose is the first rail to adopt the renderer-key
    // seam: instead of core naming 'compose' in a hardcoded railMode ===
    // arm, the descriptor points at keys the compose-rail.js fragment
    // registers on window.__bowireRailRenderers. Core stops knowing the
    // rail exists — the whole point of the pluggable-workbench cut-over.
    /// <inheritdoc />
    public string? SidebarRendererKey => "composeLibrarySidebar";
    /// <inheritdoc />
    public string? MainPaneRendererKey => "composeMain";
    /// <inheritdoc />
    public bool AlwaysOn => true;
    /// <inheritdoc />
    // Compose's request-builder tabs persist per workspace — without
    // one, the tab strip is empty and saving a new tab has nowhere to
    // land. Orthogonal to AlwaysOn: the rail can't be disabled at all,
    // but its view still needs a workspace to be useful.
    public bool RequiresWorkspace => true;
}
