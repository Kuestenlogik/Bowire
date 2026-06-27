// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rail.Compose;

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
    public string SidebarKind => "none";
    /// <inheritdoc />
    public bool AlwaysOn => true;
    /// <inheritdoc />
    // Compose's request-builder tabs persist per workspace — without
    // one, the tab strip is empty and saving a new tab has nowhere to
    // land. Orthogonal to AlwaysOn: the rail can't be disabled at all,
    // but its view still needs a workspace to be useful.
    public bool RequiresWorkspace => true;
}
