// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rails;

/// <summary>
/// Workspaces rail contribution — the workspace-navigation hub (the
/// closest thing to a file tree in Bowire), core-resident by design.
/// </summary>
/// <remarks>
/// Folded into <c>Kuestenlogik.Bowire</c> (Core) alongside Home + Discover
/// (#368, the #306 Phase G tail). Workspaces is the workspace-navigation
/// hub whose detail pane dispatches into the Collections / Environments /
/// Recordings / Sources / Settings sub-views, so — like Home and Discover
/// — it must always be present rather than shipping in a thin optional
/// package while its JS lives in core. The previous descriptor-only
/// <c>Kuestenlogik.Bowire.Workspaces</c> package has been retired; the
/// <see cref="Id"/> string stays <c>"workspaces"</c> verbatim so
/// operator-saved <c>railMode</c> + <c>bowire_enabled_rails</c> values
/// keep dispatching correctly.
/// </remarks>
public sealed class BowireWorkspacesRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "workspaces";
    /// <inheritdoc />
    public string DisplayName => "Workspaces";
    /// <inheritdoc />
    public string IconKey => "layers";
    /// <inheritdoc />
    public int SortIndex => 1200;
    /// <inheritdoc />
    /// <remarks>
    /// Own 'admin' group — separated from Security's 'hardening' group
    /// because the Workspaces switcher is a project/context surface,
    /// not an audit/scan surface. Sits just above Settings (which has
    /// its own divider at the rail foot), so admin + global config
    /// cluster visually at the bottom. Operator feedback: 'workspaces
    /// in eigene gruppe'.
    /// </remarks>
    public string Group => "admin";
    /// <inheritdoc />
    public string SidebarKind => "workspaces";
    /// <inheritdoc />
    public bool AlwaysOn => true;
}
