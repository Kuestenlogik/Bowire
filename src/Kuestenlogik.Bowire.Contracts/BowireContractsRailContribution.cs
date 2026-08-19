// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Contracts;

/// <summary>
/// #364 — the Contracts rail: a consumer × provider matrix of contract
/// verification results with per-interaction drill-in. Discovered by
/// <c>BowireRailRegistry.Discover</c>'s assembly scan (public sealed type
/// with a default constructor), so referencing the package is all a host
/// has to do.
/// </summary>
public sealed class BowireContractsRailContribution : IBowireRailContribution
{
    /// <inheritdoc />
    public string Id => "contracts";

    /// <inheritdoc />
    public string DisplayName => "Contracts";

    /// <summary>
    /// A handshake — the agreement between a consumer and a provider.
    /// Deliberately not the plain checkmark the Lint rail used to share:
    /// two rails with the same glyph are indistinguishable in the rail.
    /// </summary>
    public string IconKey => "handshake";

    /// <inheritdoc />
    public int SortIndex => 910;

    /// <summary>Contract verification is a quality gate, alongside Lint.</summary>
    public string Group => "quality";

    /// <summary>The matrix owns the whole pane; no sidebar of its own.</summary>
    public string SidebarKind => "none";

    /// <summary>
    /// Matches the key <c>contract-matrix.js</c> registers on
    /// <c>window.__bowireRailRenderers</c>.
    /// </summary>
    public string MainPaneRendererKey => "contractsMain";
}
