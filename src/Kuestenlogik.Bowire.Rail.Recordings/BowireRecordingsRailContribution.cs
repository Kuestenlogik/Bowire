// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rail.Recordings;

/// <summary>
/// Recordings rail contribution (#306 Phase G).
/// </summary>
public sealed class BowireRecordingsRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "recordings";
    /// <inheritdoc />
    public string DisplayName => "Recordings";
    /// <inheritdoc />
    public string IconKey => "recording";
    /// <inheritdoc />
    public int SortIndex => 600;
    /// <inheritdoc />
    public string Group => "scenarios";
    /// <inheritdoc />
    public string SidebarKind => "recordings";
}
