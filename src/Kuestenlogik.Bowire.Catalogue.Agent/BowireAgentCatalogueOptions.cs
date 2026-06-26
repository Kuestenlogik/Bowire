// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Catalogue.Agent;

/// <summary>
/// Options for <see cref="AgentCatalogueProvider"/> (#305 Phase E).
/// Bound from <c>Bowire:Discovery:Catalogue:Agent</c>.
/// </summary>
/// <remarks>
/// <para>
/// The agent hub itself is in flight (#128). The wire shape below is
/// the contract this provider will speak once the hub ships its
/// <c>GET {HubUrl}/hub/agents/catalogue</c> aggregator endpoint:
/// </para>
/// <code>
/// {
///   "version": 1,
///   "agents": [
///     {
///       "agentId": "surgewave-broker@eu-central",
///       "serviceName": "surgewave-broker",
///       "tags": ["env:prod", "region:eu-central"],
///       "entries": [
///         { "url": "https://surgewave-broker.internal:7080" }
///       ]
///     }
///   ]
/// }
/// </code>
/// <para>
/// Until #128 lands the provider returns an empty list and logs at
/// Debug level — the operator sees the same "no catalogue" surface
/// the unconfigured-provider path produces. Installations that
/// want to enable this provider today against a non-hub endpoint
/// can do so via <see cref="StubResponse"/>, which the test seam
/// uses to verify the wire-shape parsing.
/// </para>
/// </remarks>
public sealed class BowireAgentCatalogueOptions
{
    /// <summary>
    /// URL of the Bowire Agent hub. Required once the hub-side
    /// catalogue aggregator from #128 ships; the provider GETs
    /// <c>{HubUrl}/hub/agents/catalogue</c> on every refresh.
    /// </summary>
    public string? HubUrl { get; set; }

    /// <summary>
    /// Optional bootstrap token sent in the <c>Authorization</c>
    /// header. The hub-side authn story for #128 is still
    /// converging — Phase 1 is a shared bootstrap token (this field);
    /// mTLS is the hardening step. Sent verbatim so the operator
    /// picks the scheme.
    /// </summary>
    public string? BootstrapToken { get; set; }

    /// <summary>
    /// Per-fetch timeout. Defaults to 10 s — same shape as the
    /// http / consul providers in core.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Test seam: pre-canned JSON payload to feed the parser with.
    /// When set, the provider skips the HTTP call entirely and
    /// deserialises this string instead. Lets installations sanity-
    /// check the wire-shape contract against a static JSON snapshot
    /// before the hub-side aggregator from #128 is live.
    /// </summary>
    public string? StubResponse { get; set; }
}
