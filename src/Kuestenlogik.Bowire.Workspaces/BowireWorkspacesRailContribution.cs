// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Workspaces;

/// <summary>
/// Workspaces rail contribution (#306 Phase G). Always-on — the workspace
/// switcher is the closest thing to a file tree in Bowire and operators
/// expect it on by default.
/// </summary>
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
