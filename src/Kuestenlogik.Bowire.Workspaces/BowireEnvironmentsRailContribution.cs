// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Workspaces;

/// <summary>
/// Environments rail contribution — hidden from the rail strip; surfaces
/// inside its workspace (#306 Phase G; folded into the Workspaces package
/// in v2.1, #325 — environment variables are workspace-scoped so they
/// belong with the workspace switcher).
/// </summary>
public sealed class BowireEnvironmentsRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "environments";
    /// <inheritdoc />
    public string DisplayName => "Environments";
    /// <inheritdoc />
    public string IconKey => "globe";
    /// <inheritdoc />
    public int SortIndex => 500;
    /// <inheritdoc />
    public string Group => "work";
    /// <inheritdoc />
    public string SidebarKind => "environments";
    /// <inheritdoc />
    public bool HideFromRail => true;
}
