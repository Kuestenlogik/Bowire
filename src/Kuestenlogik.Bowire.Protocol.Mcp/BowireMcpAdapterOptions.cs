// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.Mcp;

/// <summary>
/// Adapter-only configuration for
/// <c>MapBowireMcpAdapter</c>. Independent of
/// <c>Kuestenlogik.Bowire.Mcp.BowireMcpOptions</c> so the two
/// endpoints (full-server vs adapter) can be tuned without
/// interfering with each other — see #287.
/// </summary>
/// <remarks>
/// <para>
/// Resolved via the standard <c>IOptions&lt;T&gt;</c> pipeline so
/// hosts can bind it from <c>appsettings.json</c>
/// (<c>Bowire:McpAdapter:*</c> by convention) or wire it inline:
/// </para>
/// <code>
/// builder.Services.AddBowireMcpAdapter(opts =&gt;
/// {
///     opts.UpstreamServerUrl = "http://workstation.local:5005";
///     opts.RequestTimeout = TimeSpan.FromSeconds(30);
///     opts.BearerToken = builder.Configuration["MyApp:McpAdapterToken"];
/// });
/// </code>
/// <para>
/// All properties have safe defaults. The legacy
/// <c>AddBowireMcpAdapter(string? serverUrl)</c> overload writes the
/// URL into this options block too, so old call-sites continue to
/// resolve the same value via <see cref="UpstreamServerUrl"/>.
/// </para>
/// </remarks>
public sealed class BowireMcpAdapterOptions
{
    /// <summary>
    /// URL of the upstream Bowire workbench / API host the adapter
    /// discovers + invokes against. Defaults to <c>http://localhost</c>
    /// — the legacy <c>WithMcpAdapter()</c> behaviour before the
    /// official-SDK migration.
    /// </summary>
    public string UpstreamServerUrl { get; set; } = "http://localhost";

    /// <summary>
    /// Maximum time the adapter waits for a single upstream discovery
    /// or invoke call before giving up. Default 30 s. Tighten in
    /// container hosts where a hung upstream would otherwise stall
    /// the adapter's HTTP transport thread.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Optional bearer token the adapter forwards to the upstream
    /// host on every discovery / invoke call. Set when the upstream
    /// requires authentication (Bowire auth gate, gateway token,
    /// service-to-service JWT). Leave <see langword="null"/> for
    /// open dev workbenches.
    /// </summary>
    /// <remarks>
    /// Stored in plaintext on the options instance for simplicity —
    /// inject from a secret store on the binding side
    /// (<c>builder.Configuration["…"]</c> against a binding to Azure
    /// Key Vault / AWS Secrets Manager) rather than committing the
    /// literal value.
    /// </remarks>
    public string? BearerToken { get; set; }
}
