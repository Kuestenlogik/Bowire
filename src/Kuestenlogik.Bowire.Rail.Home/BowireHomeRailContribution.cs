// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rail.Home;

/// <summary>
/// Home rail contribution — cross-workflow landing surface (#306 Phase G).
/// </summary>
/// <remarks>
/// Moved out of <c>Kuestenlogik.Bowire/Plugins/BuiltInRails.cs</c> into its
/// own NuGet package. Embedded hosts that don't reference
/// <c>Kuestenlogik.Bowire.Rail.Home</c> don't get the Home rail in the
/// catalogue and don't pay for the rail strip slot.
/// </remarks>
public sealed class BowireHomeRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "home";
    /// <inheritdoc />
    public string DisplayName => "Home";
    /// <inheritdoc />
    public string IconKey => "house";
    /// <inheritdoc />
    public int SortIndex => 100;
    /// <inheritdoc />
    /// <remarks>
    /// Own 'home' group so the rail-divider logic in render-sidebar.js
    /// draws a separator BELOW Home (the group-change boundary),
    /// visually anchoring it at the top the way Settings is anchored
    /// at the bottom. Operator feedback: 'home sollte ebenso abgesetzt
    /// sein wie einstellungen mit trennstrich'.
    /// </remarks>
    public string Group => "home";
    /// <inheritdoc />
    public string SidebarKind => "none";
    /// <inheritdoc />
    public bool AlwaysOn => true;
}
