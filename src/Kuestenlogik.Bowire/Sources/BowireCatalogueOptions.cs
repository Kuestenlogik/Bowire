// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Sources;

/// <summary>
/// Configuration root for the catalogue-provider seam (#136). Bound
/// from <c>Bowire:Discovery:Catalogue</c> by
/// <c>AddBowireCatalogue()</c>.
/// </summary>
/// <remarks>
/// <para>
/// Provider-specific settings live under
/// <c>Bowire:Discovery:Catalogue:&lt;Id&gt;</c> — each concrete
/// provider binds its own options class (e.g.
/// <see cref="BowireLocalCatalogueOptions"/> for the <c>local</c>
/// provider). The shape is intentionally similar to
/// <c>Bowire:Auth</c> — operators only have to learn one config
/// pattern.
/// </para>
/// </remarks>
public sealed class BowireCatalogueOptions
{
    /// <summary>
    /// Id of the active <see cref="IBowireCatalogueProvider"/>. Empty
    /// / null means "no catalogue" — Bowire's default, equivalent to
    /// the existing manual-URL-entry behaviour.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// How often the catalogue is re-fetched in the background. Defaults
    /// to 5 minutes. Manual refresh from the workbench is always
    /// available regardless of this setting.
    /// </summary>
    /// <remarks>
    /// Zero or negative values disable the background refresh loop —
    /// the catalogue is fetched once on startup and then only on
    /// manual triggers from the UI.
    /// </remarks>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How catalogue entries compose with locally-entered URLs (#92).
    /// Defaults to <see cref="BowireCatalogueVisibility.Editable"/> —
    /// users can add ad-hoc URLs alongside the catalogue.
    /// </summary>
    public BowireCatalogueVisibility Visibility { get; set; } = BowireCatalogueVisibility.Editable;
}

/// <summary>
/// Composition mode for catalogue entries vs. locally-entered URLs.
/// Mirrors the <c>Bowire:Discovery:UrlManagement</c> tri-state from #92.
/// </summary>
public enum BowireCatalogueVisibility
{
    /// <summary>
    /// User can add ad-hoc URLs <i>alongside</i> the catalogue
    /// entries (the default). The Sources rail shows both, each row
    /// carrying an origin chip so operators can tell them apart.
    /// </summary>
    Editable,

    /// <summary>
    /// User sees the catalogue list but can't add, edit, or remove
    /// rows. Useful for shared / hardened deployments where the
    /// operator wants a fixed in-scope target list.
    /// </summary>
    Readonly,

    /// <summary>
    /// Catalogue drives discovery silently — the URL management view
    /// is hidden entirely. Useful for embedded hosts where the
    /// surrounding org's service registry IS the authoritative
    /// source.
    /// </summary>
    Hidden,
}

/// <summary>
/// Options for the built-in <see cref="LocalCatalogueProvider"/> —
/// the path to a JSON file on disk holding a
/// <see cref="BowireCatalogueDocument"/>.
/// </summary>
/// <remarks>
/// Bound from <c>Bowire:Discovery:Catalogue:Local</c>. When
/// <see cref="Path"/> is null the provider falls back to
/// <c>~/.bowire/catalogue.json</c> — same pattern as the schema-hints
/// file. A missing file is not an error; it just resolves to an
/// empty catalogue. This lets a host enable the provider before any
/// catalogue file exists and have the first write light up
/// automatically on the next refresh.
/// </remarks>
public sealed class BowireLocalCatalogueOptions
{
    /// <summary>
    /// Absolute or relative path to the catalogue JSON document.
    /// When null, defaults to <c>~/.bowire/catalogue.json</c>.
    /// </summary>
    public string? Path { get; set; }
}

/// <summary>
/// Options for the built-in <see cref="HttpCatalogueProvider"/> — a
/// remote URL that returns a <see cref="BowireCatalogueDocument"/>
/// as JSON.
/// </summary>
/// <remarks>
/// Bound from <c>Bowire:Discovery:Catalogue:Http</c>. The fetch is a
/// straight HTTP GET — auth (bearer token, mTLS, custom header) is
/// out of scope for the v1 surface. Operators that need an authn-gated
/// endpoint can stand up a small relay or wait for a provider-side
/// auth extension. Standard ASP.NET options-binding rules apply.
/// </remarks>
public sealed class BowireHttpCatalogueOptions
{
    /// <summary>
    /// URL the provider GETs every refresh interval. Required when
    /// the <c>http</c> provider is selected; the provider returns an
    /// empty list (no throw) when the URL is missing so a half-
    /// configured host doesn't crash at startup.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Optional <c>Authorization</c> header value (e.g.
    /// <c>"Bearer eyJ..."</c>). Sent verbatim — the operator picks
    /// the scheme.
    /// </summary>
    public string? Authorization { get; set; }

    /// <summary>
    /// Per-fetch timeout. Defaults to 10 s — the workbench's
    /// refresh window is far longer (default 5 min) but a wedged
    /// upstream must not pin the refresh loop indefinitely.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Options for the built-in <see cref="ConsulCatalogueProvider"/> —
/// a Consul agent URL the provider queries via the v1 catalogue API.
/// </summary>
/// <remarks>
/// <para>
/// Bound from <c>Bowire:Discovery:Catalogue:Consul</c>. The provider
/// hits <c>GET {Address}/v1/catalog/services</c> followed by per-
/// service <c>GET {Address}/v1/catalog/service/{name}</c> calls to
/// materialise URL entries. The Consul HTTP API is stable across
/// 1.x releases so the v1 paths are hard-coded.
/// </para>
/// <para>
/// Auth: optional Consul ACL token via <see cref="Token"/>, sent in
/// the <c>X-Consul-Token</c> header. mTLS / cert auth is out of
/// scope for v1 — operators that need it can run Bowire behind a
/// proxy that adds the cert.
/// </para>
/// </remarks>
public sealed class BowireConsulCatalogueOptions
{
    /// <summary>
    /// Consul agent address — e.g. <c>"http://localhost:8500"</c>.
    /// Required when the <c>consul</c> provider is selected; falls
    /// back to <c>"http://localhost:8500"</c> when null (Consul's
    /// default loopback bind).
    /// </summary>
    public string? Address { get; set; }

    /// <summary>
    /// Optional Consul ACL token, sent as <c>X-Consul-Token</c>.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// Optional datacenter filter. When set, every catalogue API
    /// call is made with <c>?dc={Datacenter}</c>. Defaults to null
    /// (Consul picks the local DC).
    /// </summary>
    public string? Datacenter { get; set; }

    /// <summary>
    /// Optional tag filter. When set, only services that carry this
    /// tag are surfaced. Useful for scoping the catalogue to a
    /// specific environment (e.g. <c>"bowire"</c>, <c>"staging"</c>)
    /// without inflating every service the cluster runs.
    /// </summary>
    public string? Tag { get; set; }

    /// <summary>
    /// URL scheme to use when materialising the per-service URL.
    /// Defaults to <c>"http"</c> — Consul's catalogue doesn't carry
    /// the scheme intrinsically, so the operator picks one. Set to
    /// <c>"https"</c> for TLS-fronted services.
    /// </summary>
    public string Scheme { get; set; } = "http";

    /// <summary>
    /// Per-fetch timeout. Defaults to 10 s — same reasoning as
    /// <see cref="BowireHttpCatalogueOptions.Timeout"/>.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}
