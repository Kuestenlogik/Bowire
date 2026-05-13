// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.App.Configuration;

/// <summary>
/// Typed configuration for <c>bowire</c>'s browser-UI mode (the default
/// when no subcommand is given). Bound from the shared
/// <see cref="BowireConfiguration"/> stack — appsettings.json +
/// <c>BOWIRE_*</c> env vars + CLI flags — so every knob reads from one
/// place regardless of how the user configured it.
/// </summary>
/// <remarks>
/// <para>
/// Keys sit under the <c>Bowire</c> section:
/// </para>
/// <code>
/// {
///   "Bowire": {
///     "Port": 5080,
///     "Title": "My Bowire",
///     "ServerUrl": "http://api.example.com",
///     "NoBrowser": false,
///     "EnableMcpAdapter": false
///   }
/// }
/// </code>
/// <para>
/// The matching CLI flags (<c>--port</c>, <c>--title</c>, <c>--url</c>,
/// <c>--no-browser</c>, <c>--enable-mcp-adapter</c>) and prefixed env
/// vars (<c>BOWIRE_Bowire__Port</c> etc.) resolve to the same keys via
/// <see cref="BowireConfiguration.Build"/>.
/// </para>
/// </remarks>
internal sealed class BrowserUiOptions
{
    /// <summary>TCP port the local HTTP server binds to. Defaults to 5080.</summary>
    public int Port { get; set; } = 5080;

    /// <summary>Title shown in the browser tab + UI header. Defaults to "Bowire".</summary>
    public string Title { get; set; } = "Bowire";

    /// <summary>
    /// Single discovery URL. Populated from <c>--url</c> / <c>appsettings</c>.
    /// When multiple <c>--url</c> flags are passed, the first one lands here;
    /// the rest go into <see cref="ServerUrls"/>.
    /// </summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// All configured discovery URLs. Populated by repeated <c>--url</c>
    /// flags or by an array in appsettings.json:
    /// <code>
    /// "Bowire": { "ServerUrls": ["http://a", "http://b"] }
    /// </code>
    /// When non-empty, the browser-UI locks discovery to this list
    /// (users can't point it at arbitrary hosts from the landing page).
    /// </summary>
    public List<string> ServerUrls { get; set; } = [];

    /// <summary>
    /// Plugin ids to skip when scanning for protocol implementations.
    /// Bound from <c>Bowire:DisabledPlugins</c> in appsettings.json
    /// and merged with any <c>--disable-plugin</c> CLI flags by
    /// <see cref="BowireConfiguration.BuildBrowserUiOptions"/>.
    /// </summary>
    public List<string> DisabledPlugins { get; set; } = [];

    /// <summary>
    /// Suppress auto-opening the browser on startup. Also implied in
    /// headless environments (<c>DOTNET_RUNNING_IN_CONTAINER</c>,
    /// <c>CI</c>, or when the process isn't user-interactive).
    /// </summary>
    public bool NoBrowser { get; set; }

    /// <summary>
    /// Expose the discovered services as MCP tools at
    /// <c>/bowire/mcp</c> (opt-in — off by default so a Bowire pointed
    /// at an internal API isn't silently reachable by local MCP clients).
    /// </summary>
    public bool EnableMcpAdapter { get; set; }

    /// <summary>
    /// Resolved plugin directory. Populated separately by
    /// <see cref="BowireConfiguration.PluginDir"/> so its three-source
    /// precedence (flag → legacy env → appsettings → default) stays in
    /// one place.
    /// </summary>
    public string? PluginDir { get; set; }

    /// <summary>
    /// Basemap alias / tile-URL / style-URL forwarded to the MapLibre
    /// widget via <c>window.__BOWIRE_CONFIG__.mapBasemap</c>. Bound from
    /// <c>Bowire:MapBasemap</c> in appsettings.json and overrideable via
    /// the <c>--map-basemap</c> CLI flag. See
    /// <see cref="BowireOptions.MapBasemap"/> for the accepted shapes.
    /// </summary>
    public string? MapBasemap { get; set; }

    /// <summary>
    /// True when the user (or config) locked discovery to a fixed URL
    /// list — the UI disables the URL input in that case.
    /// </summary>
    public bool LockServerUrl => ServerUrls.Count > 0;

    /// <summary>Primary URL for status messages and the MCP adapter's default base.</summary>
    public string PrimaryUrl => ServerUrl ?? ServerUrls.FirstOrDefault() ?? "";
}
