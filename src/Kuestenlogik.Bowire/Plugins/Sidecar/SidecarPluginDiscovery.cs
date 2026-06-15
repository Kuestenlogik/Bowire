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
                if (logger is not null)
                    SidecarDiscoveryLog.ManifestMissingOrMalformed(logger, manifestPath);
                continue;
            }

            if (string.IsNullOrEmpty(manifest.Protocol?.Id)
                || string.IsNullOrEmpty(manifest.Executable))
            {
                if (logger is not null)
                    SidecarDiscoveryLog.ManifestMissingRequiredFields(logger, manifestPath);
                continue;
            }

            if (disabled.Contains(manifest.Protocol.Id))
            {
                if (logger is not null)
                    SidecarDiscoveryLog.SkippingDisabledSidecar(logger, manifest.Protocol.Id);
                continue;
            }

            results.Add(new SidecarBowireProtocol(manifest, subDir));
        }

        return results;
    }
}

/// <summary>
/// Source-generated logger wrappers for
/// <see cref="SidecarPluginDiscovery"/>. Folds three CA1873 sites into
/// one place; the generator emits <c>IsEnabled</c>-gated dispatch so
/// the runtime null-check the analyzer flagged is gone.
/// </summary>
internal static partial class SidecarDiscoveryLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Sidecar manifest at {Path} is missing or malformed; skipping.")]
    public static partial void ManifestMissingOrMalformed(ILogger logger, string path);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Sidecar manifest at {Path} is missing protocol.id or executable; skipping.")]
    public static partial void ManifestMissingRequiredFields(ILogger logger, string path);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Skipping disabled sidecar plugin '{PluginId}' (Bowire:DisabledPlugins).")]
    public static partial void SkippingDisabledSidecar(ILogger logger, string pluginId);
}
