// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rail.Mocks;

/// <summary>
/// Mocks rail contribution (#306 Phase G).
/// </summary>
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
}
