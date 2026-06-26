// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rail.Proxy;

/// <summary>
/// Proxy / MITM rail contribution (#306 Phase G).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deprecated in #315.</b> Folded into the unified
/// <c>BowireTrafficRailContribution</c> in
/// <c>Kuestenlogik.Bowire.Rail.Traffic</c>, which adapts to the active
/// deployment (Standalone proxy mode vs Embedded middleware mode) from
/// <c>BowireOptions.Mode</c> instead of forcing two separate rails on
/// every workbench. The boot-migration block in <c>prologue.js</c>
/// rewrites <c>localStorage.bowire_rail_mode='proxy'</c> to
/// <c>'traffic'</c> on first paint.
/// </para>
/// <para>
/// Kept registered (and <see cref="HideFromRail"/> = <c>true</c>) for one
/// release window so embedded hosts that explicitly reference the type
/// in DI (<c>services.AddBowireRail&lt;BowireProxyRailContribution&gt;()</c>)
/// keep compiling. Remove this type one minor after the deprecation
/// window expires.
/// </para>
/// </remarks>
[Obsolete("Use BowireTrafficRailContribution (Kuestenlogik.Bowire.Rail.Traffic) instead. The Traffic rail unifies Proxy + Intercepted and adapts to BowireOptions.Mode at render time. See #315.")]
public sealed class BowireProxyRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "proxy";
    /// <inheritdoc />
    public string DisplayName => "Proxy / MITM";
    /// <inheritdoc />
    public string IconKey => "disconnect";
    /// <inheritdoc />
    public int SortIndex => 900;
    /// <inheritdoc />
    public string Group => "quality";
    /// <inheritdoc />
    public string SidebarKind => "proxy";
    /// <inheritdoc />
    /// <remarks>
    /// Hidden from the rail strip during the deprecation window so the
    /// new Traffic rail is the only visible surface, but the descriptor
    /// stays in the catalogue so legacy deep links + tour scripts
    /// targeting <c>railMode='proxy'</c> still resolve.
    /// </remarks>
    public bool HideFromRail => true;
}
