// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rail.Workspaces;

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
    public string Group => "hardening";
    /// <inheritdoc />
    public string SidebarKind => "workspaces";
    /// <inheritdoc />
    public bool AlwaysOn => true;
}
