// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Mock;

/// <summary>
/// Mocks rail contribution (#306 Phase G).
/// </summary>
/// <remarks>
/// Lives on <c>Kuestenlogik.Bowire.Mock</c> alongside the mock-host
/// runtime — the provisional standalone <c>Kuestenlogik.Bowire.Rail.Mocks</c>
/// package was folded in for v2.1 so embedded hosts that want the
/// Mocks rail simply reference <c>Kuestenlogik.Bowire.Mock</c>.
/// </remarks>
public sealed class BowireMocksRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "mocks";
    /// <inheritdoc />
    public string DisplayName => "Mocks";
    /// <inheritdoc />
    public string IconKey => "mock";
    /// <inheritdoc />
    public int SortIndex => 700;
    /// <inheritdoc />
    public string Group => "scenarios";
    /// <inheritdoc />
    public string SidebarKind => "mocks";
    /// <inheritdoc />
    // Mocks bind to the active workspace's mock catalogue; without one
    // there's nowhere to read from or save to.
    public bool RequiresWorkspace => true;
}
