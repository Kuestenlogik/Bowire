// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rail.Intercepted;

/// <summary>
/// Intercepted rail contribution (#153 Phase A+B; extracted in #306 Phase G).
/// </summary>
/// <remarks>
/// In-process middleware sister to the standalone Proxy rail. Surfaces every
/// request flowing through a host that opted in via
/// <c>app.UseBowireInterceptor()</c>. The <c>/api/intercepted/*</c> endpoints
/// always mount (via <c>MapBowire</c>); when the host never opted in the store
/// stays empty and the rail renders a "no traffic yet" empty card.
/// </remarks>
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
}
