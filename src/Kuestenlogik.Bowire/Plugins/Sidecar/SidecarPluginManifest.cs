// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Plugins.Sidecar;

/// <summary>
/// The <c>sidecar.json</c> manifest shape that marks a directory under
/// <c>~/.bowire/plugins/</c> as a sidecar (non-.NET) plugin. See
/// <c>docs/architecture/sidecar-plugins.md</c> for the full schema.
/// </summary>
/// <param name="PackageId">Reverse-DNS package id (e.g. <c>Acme.Bowire.Protocol.Zenoh</c>).</param>
/// <param name="Protocol">Protocol metadata the manifest declares before the sidecar starts.</param>
/// <param name="Executable">Path to the sidecar executable, relative to the plugin directory. Required for the <c>stdio</c> transport.</param>
/// <param name="Args">Extra args appended to the executable command line. <c>null</c> = none.</param>
/// <param name="EnvPrefix">Env-var prefix forwarded to the subprocess. Default <c>BOWIRE_</c>.</param>
/// <param name="ShutdownTimeoutMs">Grace period after <c>shutdown</c> before force-kill (stdio). Default 3000.</param>
/// <param name="Version">Optional version string for display in <c>bowire plugin list</c>. Sidecars carry no NuGet version, so this is the only version source.</param>
/// <param name="Transport">Wire transport: <c>"stdio"</c> (default — spawn <c>Executable</c>, JSON-RPC over stdin/stdout) or <c>"http"</c> (POST JSON-RPC to <c>Url</c>, notifications over SSE).</param>
/// <param name="Url">For the <c>http</c> transport: the JSON-RPC endpoint (e.g. <c>http://localhost:7000/bowire</c>). Required when <c>Transport == "http"</c>, ignored otherwise.</param>
public sealed record SidecarPluginManifest(
    [property: JsonPropertyName("packageId")] string PackageId,
    [property: JsonPropertyName("protocol")] SidecarProtocolMetadata Protocol,
    [property: JsonPropertyName("executable")] string Executable = "",
    [property: JsonPropertyName("args")] IReadOnlyList<string>? Args = null,
    [property: JsonPropertyName("envPrefix")] string EnvPrefix = "BOWIRE_",
    [property: JsonPropertyName("shutdownTimeoutMs")] int ShutdownTimeoutMs = 3000,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("transport")] string Transport = "stdio",
    [property: JsonPropertyName("url")] string? Url = null)
{
    /// <summary>True when this manifest declares the HTTP/SSE transport (vs stdio subprocess).</summary>
    public bool IsHttp => string.Equals(Transport, "http", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Manifest filename that marks a plugin directory as a sidecar.
    /// Distinct from the NuGet-install <c>plugin.json</c> metadata file
    /// so the two never collide in their (mutually exclusive) plugin
    /// directories.
    /// </summary>
    public const string FileName = "sidecar.json";

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

    /// <summary>Parse a manifest from a JSON string. Returns <c>null</c> on malformed input.</summary>
    public static SidecarPluginManifest? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<SidecarPluginManifest>(json, s_jsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the manifest has the minimum fields to load: a protocol
    /// id plus the transport-specific target — an executable for stdio,
    /// a url for http.
    /// </summary>
    public bool IsValid =>
        !string.IsNullOrEmpty(Protocol?.Id)
        && (IsHttp ? !string.IsNullOrEmpty(Url) : !string.IsNullOrEmpty(Executable));

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
