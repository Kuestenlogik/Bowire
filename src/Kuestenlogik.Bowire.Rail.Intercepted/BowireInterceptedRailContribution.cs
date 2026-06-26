// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rail.Intercepted;

/// <summary>
/// Intercepted rail contribution (#153 Phase A+B; extracted in #306 Phase G).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deprecated in #315.</b> Folded into the unified
/// <c>BowireTrafficRailContribution</c> in
/// <c>Kuestenlogik.Bowire.Rail.Traffic</c>, which adapts to the active
/// deployment (Standalone proxy mode vs Embedded middleware mode) from
/// <c>BowireOptions.Mode</c>. The boot-migration block in
/// <c>prologue.js</c> rewrites
/// <c>localStorage.bowire_rail_mode='intercepted'</c> to <c>'traffic'</c>
/// on first paint.
/// </para>
/// <para>
/// Kept registered (and <see cref="HideFromRail"/> = <c>true</c>) for one
/// release window so embedded hosts that explicitly reference the type
/// in DI keep compiling. Remove this type one minor after the
/// deprecation window expires.
/// </para>
/// </remarks>
[Obsolete("Use BowireTrafficRailContribution (Kuestenlogik.Bowire.Rail.Traffic) instead. The Traffic rail unifies Proxy + Intercepted and adapts to BowireOptions.Mode at render time. See #315.")]
public sealed class BowireInterceptedRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "intercepted";
    /// <inheritdoc />
    public string DisplayName => "Intercepted";
    /// <inheritdoc />
    public string IconKey => "globe";
    /// <inheritdoc />
    public int SortIndex => 950;
    /// <inheritdoc />
    public string Group => "quality";
    /// <inheritdoc />
    public string SidebarKind => "intercepted";
    /// <inheritdoc />
    /// <remarks>
    /// Hidden from the rail strip during the deprecation window so the
    /// new Traffic rail is the only visible surface, but the descriptor
    /// stays in the catalogue so legacy deep links + tour scripts
    /// targeting <c>railMode='intercepted'</c> still resolve.
    /// </remarks>
    public bool HideFromRail => true;
}
