// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.App.Configuration;

/// <summary>
/// Typed configuration for the <c>bowire plugin</c> subcommand.
/// Carries the positional sub-command (<c>install</c>/<c>list</c>/
/// <c>uninstall</c>) and package id, plus the <c>--version</c> flag
/// for installs. Bound from the <c>Bowire:Plugin</c> config section
/// — useful in CI scenarios where a scripted install wants to pin the
/// version via appsettings.json.
/// </summary>
internal sealed class PluginCliOptions
{
    /// <summary>Sub-command: <c>install</c>, <c>list</c>, or <c>uninstall</c>.</summary>
    public string Command { get; set; } = "";

    /// <summary>NuGet package id (for <c>install</c>/<c>uninstall</c>).</summary>
    public string PackageId { get; set; } = "";

    /// <summary>Optional NuGet version for <c>install</c>. Defaults to latest.</summary>
    public string? Version { get; set; }

    /// <summary>
    /// Local <c>.nupkg</c> file path for offline installs. When set,
    /// <c>install</c> reads the package id + version from the file's
    /// embedded <c>.nuspec</c> instead of resolving them through a
    /// NuGet feed; <see cref="Sources"/> is then optional and only
    /// used to pull transitive dependencies.
    /// </summary>
    public string? File { get; set; }

    /// <summary>
    /// Output directory for <c>plugin download</c>. The recursive
    /// dep-walker writes the root <c>.nupkg</c> plus every transitive
    /// dependency's <c>.nupkg</c> into this folder so it can be
    /// transferred to an air-gapped host and consumed by
    /// <c>plugin install --file &lt;root&gt;.nupkg --source &lt;outputDir&gt;</c>.
    /// </summary>
    public string? OutputDir { get; set; }

    /// <summary>
    /// NuGet feed URLs (or <c>v3/index.json</c> endpoints) used by
    /// <c>install</c>. When empty, defaults to
    /// <see cref="Plugins.NuGetPackageInstaller.DefaultSource"/>
    /// (nuget.org). Populated from repeated <c>--source</c> CLI flags
    /// and from the <c>Bowire:Plugin:Sources</c> array in
    /// <c>appsettings.json</c>; repeated CLI flags override the
    /// appsettings list entirely.
    /// </summary>
    public List<string> Sources { get; set; } = [];

    /// <summary>
    /// Expand <c>plugin list</c> output with per-plugin resolved
    /// version, install timestamp, configured sources, and file list.
    /// Bound from <c>--verbose</c> / <c>-v</c> and
    /// <c>Bowire:Plugin:Verbose</c>.
    /// </summary>
    public bool Verbose { get; set; }
}
