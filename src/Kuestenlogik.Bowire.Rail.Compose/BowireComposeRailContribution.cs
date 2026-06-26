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
}
