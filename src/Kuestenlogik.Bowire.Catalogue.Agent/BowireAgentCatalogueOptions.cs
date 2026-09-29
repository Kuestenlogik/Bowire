// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Catalogue.Agent;

/// <summary>
/// Options for <see cref="AgentCatalogueProvider"/> (#305 Phase E).
/// Bound from <c>Bowire:Discovery:Catalogue:Agent</c>.
/// </summary>
/// <remarks>
/// <para>
/// The wire shape of the hub's <c>GET {HubUrl}/hub/agents/catalogue</c>
/// (#128 — any Bowire with <c>Bowire:Hub:Enabled</c>, or
/// <c>app.MapBowireHub()</c>):
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
/// Only live agents are listed. Installations that serve this shape from
/// something other than a Bowire hub can check it with
/// <see cref="StubResponse"/>, which the test seam also uses.
/// </para>
/// </remarks>
public sealed class BowireAgentCatalogueOptions
{
    /// <summary>
    /// URL of the Bowire Agent hub; the provider GETs
    /// <c>{HubUrl}/hub/agents/catalogue</c> on every refresh.
    /// </summary>
    public string? HubUrl { get; set; }

    /// <summary>
    /// The hub's token (<c>Bowire:Hub:Token</c>), sent as
    /// <c>Authorization: Bearer …</c>. A hub with a token refuses
    /// every call without it.
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
    /// check the wire-shape contract against a static JSON snapshot.
    /// </summary>
    public string? StubResponse { get; set; }
}
