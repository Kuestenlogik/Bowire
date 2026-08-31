// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// Where a Bowire listener gets its address from (#634, #635).
/// </summary>
/// <remarks>
/// <para>
/// Two commands stand up a Kestrel of their own — the standalone workbench
/// and <c>bowire mcp serve --bind http</c> — and both used to name their
/// address outright, which put them above every configuration source ASP.NET
/// reads. An operator who set <c>ASPNETCORE_URLS</c> got the built-in default
/// instead, in plaintext, with nothing logged to say so.
/// </para>
/// <para>
/// The rule lives here rather than in either host so there is one answer to
/// "where does Bowire listen", and one place to argue with it.
/// </para>
/// </remarks>
internal static class ListenAddress
{
    /// <summary>
    /// Whether the platform's own address configuration says anything (#634).
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the four keys ASP.NET itself reads: <c>ASPNETCORE_URLS</c>
    /// and <c>--urls</c> land on <c>urls</c>, <c>ASPNETCORE_HTTP_PORTS</c> and
    /// <c>ASPNETCORE_HTTPS_PORTS</c> on <c>http_ports</c> / <c>https_ports</c>,
    /// and <c>appsettings.json</c> can describe endpoints — certificate and
    /// all — under <c>Kestrel:Endpoints</c>. Any one of them is an operator
    /// who has already said where Bowire should listen.
    /// </para>
    /// <para>
    /// Bowire's own <c>--url</c> is unrelated and does not collide: it names
    /// the services to probe and binds to <c>Bowire:ServerUrls</c>.
    /// </para>
    /// </remarks>
    internal static bool PlatformConfigured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return !string.IsNullOrWhiteSpace(configuration["urls"])
            || !string.IsNullOrWhiteSpace(configuration["http_ports"])
            || !string.IsNullOrWhiteSpace(configuration["https_ports"])
            || configuration.GetSection("Kestrel:Endpoints").GetChildren().Any();
    }

    /// <summary>
    /// The scheme the configured address asks for, for the one place that has
    /// to name a URL before Kestrel has bound one.
    /// </summary>
    internal static string ConfiguredScheme(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var urls = configuration["urls"];
        if (!string.IsNullOrWhiteSpace(urls)
            && urls.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "https";
        }

        // https_ports without urls is the documented way to ask for TLS only.
        if (!string.IsNullOrWhiteSpace(configuration["https_ports"])
            && string.IsNullOrWhiteSpace(configuration["http_ports"]))
        {
            return "https";
        }

        return "http";
    }

    /// <summary>
    /// Decide what — if anything — Bowire should pass to <c>UseUrls</c> (#634).
    /// </summary>
    /// <param name="portExplicit">Whether the operator named the port.</param>
    /// <param name="port">The port, explicit or defaulted.</param>
    /// <param name="platformConfigured">
    /// Whether <c>ASPNETCORE_URLS</c>, the port variables or
    /// <c>Kestrel:Endpoints</c> already name an address.
    /// </param>
    /// <returns>
    /// The value for <c>UseUrls</c>, or <c>null</c> to leave the platform's
    /// configuration standing; plus a line to log when Bowire overrode
    /// something the operator had configured.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Split from the host so the precedence can be tested as the table it is,
    /// rather than by standing up a server per case.
    /// </para>
    /// <para>
    /// The rule is that a port Bowire chose is not a decision and must not
    /// behave like one. <c>--port</c> is a command-line argument and outranks
    /// environment and <c>appsettings</c> — the VS Code extension passes it
    /// alongside <c>--port-file</c> and has to keep winning — but a built-in
    /// default (5080 for the workbench, 5081 for <c>mcp serve</c>) is not
    /// something anybody asked for, and applying it through <c>UseUrls</c> is
    /// what silently discarded configured HTTPS endpoints. When nothing is
    /// configured anywhere the default applies exactly as before, so a plain
    /// <c>bowire</c> is unchanged.
    /// </para>
    /// </remarks>
    internal static (string? Urls, string? Note) Resolve(
        bool portExplicit, int port, bool platformConfigured)
    {
        if (!portExplicit && platformConfigured)
        {
            // The operator's configuration stands, certificate and all.
            return (null, null);
        }

        // "localhost" is a two-address alias to Kestrel (127.0.0.1 and [::1]),
        // and it refuses to bind that dynamically — "Dynamic port binding is
        // not supported when binding to localhost". Port 0 therefore has to
        // name a concrete loopback address. IPv4 rather than [::1] because it
        // is the one every client on this machine can reach.
        var urls = port == 0
            ? "http://127.0.0.1:0"
            : string.Create(CultureInfo.InvariantCulture, $"http://localhost:{port}");

        var note = portExplicit && platformConfigured
            ? string.Create(CultureInfo.InvariantCulture,
                $"--port {port} overrides the address configured through ASPNETCORE_URLS / Kestrel:Endpoints; listening on {urls} instead. Drop --port to use the configured endpoint.")
            : null;

        return (urls, note);
    }
}
