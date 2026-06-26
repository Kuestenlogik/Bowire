// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Sources;

/// <summary>
/// Extension point for "where does Bowire's URL/service list come
/// from" (#136) — sibling to <see cref="IBowireProtocol"/> (wire
/// plugins) and <c>IBowireAuthProvider</c> (sign-in plugins). Today
/// the workbench reads URLs from local config + the user-editable
/// sidebar; a catalogue provider augments (or replaces) that source
/// by reading from a service registry, a remote JSON document, or a
/// Bowire-agent hub.
/// </summary>
/// <remarks>
/// <para>
/// Concrete providers live either in core (<c>local</c>, <c>http</c>,
/// <c>consul</c>) or in optional sibling packages
/// (<c>Kuestenlogik.Bowire.Catalogue.Kubernetes</c>,
/// <c>Kuestenlogik.Bowire.Catalogue.Agent</c>) so heavyweight client
/// libraries only land in installs that opt-in. Discovery follows the
/// same assembly-scan pattern as
/// <see cref="BowireProtocolRegistry"/>: providers are picked up at
/// startup if their assembly is loaded.
/// </para>
/// <para>
/// At most one provider is active per process — selected by the
/// <c>Bowire:Discovery:Catalogue:Provider</c> appsettings key (or the
/// <c>--catalogue-provider &lt;id&gt;</c> CLI flag once the standalone
/// tool exposes it). When no provider is selected (the laptop
/// default), the catalogue endpoint returns an empty list and the
/// workbench keeps its current "manual URL entry only" behaviour.
/// </para>
/// <para>
/// Composes with <c>Bowire:Discovery:UrlManagement</c> (#92) — catalogue
/// entries and locally-added URLs are additive, with an origin chip
/// distinguishing them in the URL management view.
/// </para>
/// </remarks>
public interface IBowireCatalogueProvider
{
    /// <summary>
    /// Stable id used to select this provider in config (e.g.
    /// <c>"local"</c>, <c>"http"</c>, <c>"consul"</c>,
    /// <c>"kubernetes"</c>, <c>"agent"</c>). Compared case-insensitively
    /// against the configured provider key.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Human-readable provider name for the Settings → Sources tab
    /// and startup logs (e.g. <c>"Local file"</c>,
    /// <c>"HTTP endpoint"</c>, <c>"Consul"</c>).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Fetch the current catalogue snapshot. Called on a configurable
    /// refresh interval (default 5 minutes; see
    /// <see cref="BowireCatalogueOptions.RefreshInterval"/>) and on a
    /// manual trigger from the workbench UI.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancellation token — refresh cycles are bounded by the
    /// configured interval, so providers should respect it to avoid
    /// piling up overlapping fetches when an upstream is slow.
    /// </param>
    /// <returns>
    /// The list of catalogue entries currently visible to this
    /// provider. Empty list is fine (means "no entries"); throw to
    /// signal a hard fetch failure — the registry catches and
    /// surfaces it as a problem-details response on
    /// <c>GET /api/catalogue/entries</c>.
    /// </returns>
    Task<IReadOnlyList<BowireCatalogueEntry>> FetchAsync(CancellationToken cancellationToken);
}
