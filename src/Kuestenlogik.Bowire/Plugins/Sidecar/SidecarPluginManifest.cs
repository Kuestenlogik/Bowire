// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Plugins.Sidecar;

/// <summary>
/// The <c>plugin.json</c> manifest shape that marks a directory under
/// <c>~/.bowire/plugins/</c> as a sidecar (non-.NET) plugin. See
/// <c>docs/architecture/sidecar-plugins.md</c> for the full schema.
/// </summary>
/// <param name="PackageId">Reverse-DNS package id (e.g. <c>Acme.Bowire.Protocol.Zenoh</c>).</param>
/// <param name="Protocol">Protocol metadata the manifest declares before the sidecar starts.</param>
/// <param name="Executable">Path to the sidecar executable, relative to the plugin directory.</param>
/// <param name="Args">Extra args appended to the executable command line. <c>null</c> = none.</param>
/// <param name="EnvPrefix">Env-var prefix forwarded to the subprocess. Default <c>BOWIRE_</c>.</param>
/// <param name="ShutdownTimeoutMs">Grace period after <c>shutdown</c> before force-kill. Default 3000.</param>
public sealed record SidecarPluginManifest(
    [property: JsonPropertyName("packageId")] string PackageId,
    [property: JsonPropertyName("protocol")] SidecarProtocolMetadata Protocol,
    [property: JsonPropertyName("executable")] string Executable,
    [property: JsonPropertyName("args")] IReadOnlyList<string>? Args = null,
    [property: JsonPropertyName("envPrefix")] string EnvPrefix = "BOWIRE_",
    [property: JsonPropertyName("shutdownTimeoutMs")] int ShutdownTimeoutMs = 3000)
{
    /// <summary>Parse a manifest from disk. Returns <c>null</c> when the file is missing or invalid.</summary>
    public static SidecarPluginManifest? TryLoadFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<SidecarPluginManifest>(fs, s_jsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>Protocol metadata declared upfront by the manifest, before <c>initialize</c> overrides it.</summary>
public sealed record SidecarProtocolMetadata(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("iconSvg")] string? IconSvg = null);
