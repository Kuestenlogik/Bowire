// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// #587 — the Rollup rail: one row per service across whatever Bowire
/// reports the operator points it at. Sits in the quality group beside Lint
/// and Contracts, because it is the same question asked one level up: those
/// two answer "is this service healthy?", this one answers it for a
/// portfolio.
/// </summary>
public sealed class BowireReportRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "rollup";

    /// <inheritdoc />
    public string DisplayName => "Rollup";

    /// <summary>
    /// Stacked layers — several services' reports folded into one view.
    /// Distinct from the Contracts certificate and the Lint squiggle so the
    /// three quality rails stay tellable apart at rail size.
    /// </summary>
    public string IconKey => "layers";

    /// <inheritdoc />
    public int SortIndex => 920;

    /// <inheritdoc />
    public string Group => "quality";

    /// <summary>The table owns the whole pane; no sidebar of its own.</summary>
    public string SidebarKind => "none";

    /// <summary>Matches the key <c>rollup.js</c> registers.</summary>
    public string? MainPaneRendererKey => "rollupMain";
}
