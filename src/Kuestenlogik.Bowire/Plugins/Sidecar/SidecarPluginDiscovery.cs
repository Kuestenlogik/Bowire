// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Plugins.Sidecar;

/// <summary>
/// Scans the user plugin directory for sidecar plugins —
/// subdirectories that contain a <c>plugin.json</c> manifest. Each
/// discovered manifest becomes one <see cref="SidecarBowireProtocol"/>
/// registered alongside the .NET plugins the assembly scanner picks up.
/// </summary>
public static class SidecarPluginDiscovery
{
    /// <summary>The conventional filename that marks a directory as a sidecar plugin.</summary>
    public const string ManifestFileName = SidecarPluginManifest.FileName;

    /// <summary>
    /// Default plugin root — <c>~/.bowire/plugins/</c>. Matches the
    /// path the existing .NET <c>PluginManager</c> uses, so a sidecar
    /// and a .NET plugin can live side-by-side under different
    /// subdirectories.
    /// </summary>
    public static string DefaultPluginRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".bowire", "plugins");

    /// <summary>
    /// Find every <c>plugin.json</c> directly under
    /// <paramref name="pluginRoot"/> and instantiate one
    /// <see cref="SidecarBowireProtocol"/> per valid manifest. Missing
    /// root directory returns an empty list (not an error — most hosts
    /// don't have sidecars installed).
    /// </summary>
    /// <param name="pluginRoot">Plugin root dir; <c>null</c> = default.</param>
    /// <param name="disabledPluginIds">Same disable list the .NET scanner uses; matched on the manifest's <c>protocol.id</c>.</param>
    /// <param name="logger">Optional logger for malformed-manifest warnings.</param>
    public static IReadOnlyList<SidecarBowireProtocol> Discover(
        string? pluginRoot = null,
        ISet<string>? disabledPluginIds = null,
        ILogger? logger = null)
    {
        var root = pluginRoot ?? DefaultPluginRoot;
        if (!Directory.Exists(root)) return [];

        var disabled = disabledPluginIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<SidecarBowireProtocol>();

        foreach (var subDir in Directory.EnumerateDirectories(root))
        {
            var manifestPath = Path.Combine(subDir, ManifestFileName);
            if (!File.Exists(manifestPath)) continue;

            var manifest = SidecarPluginManifest.TryLoadFromFile(manifestPath);
            if (manifest is null)
            {
#pragma warning disable CA1873
                logger?.LogWarning(
                    "Sidecar manifest at {Path} is missing or malformed; skipping.",
                    manifestPath);
#pragma warning restore CA1873
                continue;
            }

            if (string.IsNullOrEmpty(manifest.Protocol?.Id)
                || string.IsNullOrEmpty(manifest.Executable))
            {
#pragma warning disable CA1873
                logger?.LogWarning(
                    "Sidecar manifest at {Path} is missing protocol.id or executable; skipping.",
                    manifestPath);
#pragma warning restore CA1873
                continue;
            }

            if (disabled.Contains(manifest.Protocol.Id))
            {
#pragma warning disable CA1873
                logger?.LogInformation(
                    "Skipping disabled sidecar plugin '{PluginId}' (Bowire:DisabledPlugins).",
                    manifest.Protocol.Id);
#pragma warning restore CA1873
                continue;
            }

            results.Add(new SidecarBowireProtocol(manifest, subDir));
        }

        return results;
    }
}
