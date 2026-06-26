// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rail.Flows;

/// <summary>
/// Flows rail contribution (#306 Phase G).
/// </summary>
public sealed class BowireFlowsRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "flows";
    /// <inheritdoc />
    public string DisplayName => "Flows";
    /// <inheritdoc />
    public string IconKey => "flow";
    /// <inheritdoc />
    public int SortIndex => 800;
    /// <inheritdoc />
    public string Group => "scenarios";
    /// <inheritdoc />
    public string SidebarKind => "flows";
}
