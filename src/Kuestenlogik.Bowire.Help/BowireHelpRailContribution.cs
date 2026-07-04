// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Help;

/// <summary>
/// Help rail contribution (#324). Hoists the in-app docs out of the
/// unified right-side drawer into a full rail with the standard
/// left-sidebar + main-pane shape every other rail uses.
/// </summary>
/// <remarks>
/// <para>
/// Lives inside <c>Kuestenlogik.Bowire.Help</c> alongside the existing
/// <see cref="IBowireHelpProvider"/> implementation
/// (<c>MarkdownHelpProvider</c>). The single-package convention
/// (one topic = one NuGet) keeps the embedded-host pluggability contract
/// simple: hosts that reference <c>Kuestenlogik.Bowire.Help</c> get both
/// the docs provider AND the rail; hosts that don't reference it lose
/// both — same posture as today, no rail icon, no provider endpoints.
/// </para>
/// <para>
/// The rail's sidebar (search box + topic-tree nav) and main pane
/// (rendered markdown body, full width) are rendered by JS code that
/// lives in core's <c>help.js</c> — the descriptor only opts the rail
/// into the strip + Settings catalogue. The same morphdom click-fix
/// from commit <c>a6a0b95</c> (re-resolve topic id at click time via
/// <c>data-help-topic-id</c>) carries over to the sidebar's topic rows.
/// </para>
/// </remarks>
public sealed class BowireHelpRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "help";
    /// <inheritdoc />
    public string DisplayName => "Help";
    /// <inheritdoc />
    /// <remarks>
    /// Uses the shared <c>help</c> life-ring glyph (the same
    /// <c>helpers.js</c> <c>svgIcon</c> the topbar help button renders)
    /// so the rail and the quick-access topbar affordance read as one
    /// consistent "Help" surface — the topbar button now switches into
    /// this rail rather than toggling the old drawer.
    /// </remarks>
    public string IconKey => "help";
    /// <inheritdoc />
    /// <remarks>
    /// Sits at the bottom of the rail strip alongside the other
    /// admin / operator-info surfaces. Built-in sort indices use
    /// 100-step intervals (Home=100, Discover=200, Compose=300, …,
    /// Workspaces=1200); 9500 leaves plenty of room for future
    /// rails to wedge in front of Help without re-numbering.
    /// </remarks>
    public int SortIndex => 9500;
    /// <inheritdoc />
    /// <remarks>
    /// Own 'help' group so the rail-divider logic draws a separator
    /// above Help, visually parking it as the rail's terminal entry
    /// (mirroring the way Home is anchored at the top via its own
    /// 'home' group).
    /// </remarks>
    public string Group => "help";
    /// <inheritdoc />
    public string SidebarKind => "help";

    // #306 / #314 — renderer-key seam: core resolves these from
    // window.__bowireRailRenderers (registered by the help.js fragment
    // this package now embeds) instead of hardcoded railMode / switch arms.
    /// <inheritdoc />
    public string? SidebarRendererKey => "helpSidebar";
    /// <inheritdoc />
    public string? MainPaneRendererKey => "helpMain";

    /// <inheritdoc />
    public bool DefaultEnabled => true;
    /// <inheritdoc />
    /// <remarks>
    /// Help reads as global reference material, not workspace-scoped
    /// content — it stays reachable from a brand-new install with no
    /// workspaces yet, same as Home / Discover / Settings.
    /// </remarks>
    public bool RequiresWorkspace => false;
}
