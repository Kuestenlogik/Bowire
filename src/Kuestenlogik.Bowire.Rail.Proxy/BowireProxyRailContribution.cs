// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rail.Proxy;

/// <summary>
/// Proxy / MITM rail contribution (#306 Phase G).
/// </summary>
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
}
