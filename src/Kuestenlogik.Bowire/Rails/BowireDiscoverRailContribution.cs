// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rails;

/// <summary>
/// Discover rail contribution — schema-driven services tree (#306 Phase G;
/// folded into Core in v2.1, #325).
/// </summary>
/// <remarks>
/// Descriptor-only — no per-rail JS fragment, no wwwroot resources. The
/// previous standalone <c>Kuestenlogik.Bowire.Rail.Discover</c> package
/// has been retired; <see cref="Id"/> remains <c>"discover"</c> verbatim
/// so saved <c>railMode</c> + <c>bowire_enabled_rails</c> values keep
/// dispatching correctly.
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
