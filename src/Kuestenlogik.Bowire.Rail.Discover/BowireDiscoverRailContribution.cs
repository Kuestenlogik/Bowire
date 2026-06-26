// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rail.Discover;

/// <summary>
/// Discover rail contribution — schema-driven services tree (#306 Phase G).
/// </summary>
/// <remarks>
/// Moved out of <c>Kuestenlogik.Bowire/Plugins/BuiltInRails.cs</c> into its
/// own NuGet package.
/// </remarks>
public sealed class BowireDiscoverRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "discover";
    /// <inheritdoc />
    public string DisplayName => "Discover";
    /// <inheritdoc />
    public string IconKey => "discover";
    /// <inheritdoc />
    public int SortIndex => 200;
    /// <inheritdoc />
    public string Group => "work";
    /// <inheritdoc />
    public string SidebarKind => "services";
    /// <inheritdoc />
    public bool AlwaysOn => true;
}
