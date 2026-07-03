// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Benchmarking;

/// <summary>
/// Benchmarks rail contribution (#306 Phase G).
/// </summary>
/// <remarks>
/// The contribution Id stays <c>"benchmarks"</c> (verbatim) so
/// operator-saved <c>railMode</c> + <c>bowire_enabled_rails</c> values
/// keep dispatching unchanged across the rename — only the package id
/// + namespace changed to <c>Kuestenlogik.Bowire.Benchmarking</c>
/// (#325, v2.1). The gerund matches the activity-rail naming pattern
/// (<c>Compose</c>, <c>Mock</c>, <c>Discover</c>, <c>Help</c>,
/// <c>Telemetry</c>) — "benchmarking" reads as the thing the user does,
/// not "benchmarks of Bowire".
/// </remarks>
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

    // #306 / #314 — renderer-key seam: core resolves these from
    // window.__bowireRailRenderers (registered by benchmarks.js) instead
    // of a hardcoded railMode arm.
    /// <inheritdoc />
    public string? SidebarRendererKey => "benchmarksSidebar";
    /// <inheritdoc />
    public string? MainPaneRendererKey => "benchmarksMain";

    /// <inheritdoc />
    // Benchmark specs persist per-workspace; without one the rail can't
    // list or save anything.
    public bool RequiresWorkspace => true;
}
