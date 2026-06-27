// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Rail.Benchmarks;

/// <summary>
/// Benchmarks rail contribution (#306 Phase G).
/// </summary>
public sealed class BowireBenchmarksRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "benchmarks";
    /// <inheritdoc />
    public string DisplayName => "Benchmarks";
    /// <inheritdoc />
    public string IconKey => "chart";
    /// <inheritdoc />
    public int SortIndex => 1000;
    /// <inheritdoc />
    public string Group => "quality";
    /// <inheritdoc />
    public string SidebarKind => "benchmarks";
    /// <inheritdoc />
    // Benchmark specs persist per-workspace; without one the rail can't
    // list or save anything.
    public bool RequiresWorkspace => true;
}
