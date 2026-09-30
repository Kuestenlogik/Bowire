// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Net;

/// <summary>How outbound connections find their way out (#680).</summary>
public enum BowireProxyMode
{
    /// <summary>The operating system's settings: <c>HTTPS_PROXY</c> / <c>HTTP_PROXY</c> / <c>NO_PROXY</c>, else the OS proxy configuration.</summary>
    System,

    /// <summary>One proxy, configured in Bowire.</summary>
    Manual,

    /// <summary>Always connect directly.</summary>
    None,
}

/// <summary>
/// One layer of network settings — the global file, a workspace's override, or
/// the host configuration. Every field is optional; a layer only says what it
/// changes, and <see cref="BowireNetworkPolicy"/> lays them over each other.
/// </summary>
/// <remarks>
/// There is deliberately no password field. A proxy password is named by
/// reference (<c>keyring:service/account</c> or <c>env:VARIABLE</c>) and
/// resolved when a connection needs it, so no settings file, export or log
/// ever holds it.
/// </remarks>
public sealed class BowireNetworkSettings
{
    /// <summary><c>system</c>, <c>manual</c> or <c>none</c>.</summary>
    public string? Mode { get; set; }

    /// <summary>The proxy for <c>manual</c>, e.g. <c>http://proxy.corp:3128</c>. User info in the URL is refused.</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>Hosts that bypass the proxy, in <see cref="BowireNoProxyList"/> forms.</summary>
    public string? NoProxy { get; set; }

    /// <summary>The proxy user name, when the proxy asks for credentials.</summary>
    public string? ProxyUser { get; set; }

    /// <summary>Where the proxy password comes from: <c>keyring:service/account</c> or <c>env:VARIABLE</c>.</summary>
    public string? ProxyPasswordRef { get; set; }

    /// <summary>Extra trusted CA certificates: a path to a PEM bundle, or the PEM text itself.</summary>
    public string? CaBundle { get; set; }

    /// <summary>Whether this layer sets nothing at all.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Mode is null && ProxyUrl is null && NoProxy is null && ProxyUser is null
        && ProxyPasswordRef is null && CaBundle is null;

    /// <summary>Parse a mode name; <c>null</c> for anything unrecognised.</summary>
    public static BowireProxyMode? ParseMode(string? mode) => mode?.Trim().ToUpperInvariant() switch
    {
        "SYSTEM" => BowireProxyMode.System,
        "MANUAL" => BowireProxyMode.Manual,
        "NONE" or "DIRECT" or "OFF" => BowireProxyMode.None,
        _ => null,
    };

    /// <summary>The settings-file spelling of a mode.</summary>
    public static string ModeName(BowireProxyMode mode) => mode switch
    {
        BowireProxyMode.Manual => "manual",
        BowireProxyMode.None => "none",
        _ => "system",
    };

    /// <summary>
    /// What is wrong with this layer, or <c>null</c>. Checked before a layer is
    /// saved, so a file on disk never holds a credential or a mode Bowire
    /// cannot read.
    /// </summary>
    public string? Validate()
    {
        if (Mode is not null && ParseMode(Mode) is null)
            return "mode must be system, manual or none.";
        // "--proxy-url none" / "system" name a mode rather than a proxy.
        if (!string.IsNullOrWhiteSpace(ProxyUrl) && ParseMode(ProxyUrl) is null)
        {
            if (!Uri.TryCreate(ProxyUrl.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
                    && uri.Scheme != "socks5" && uri.Scheme != "socks4" && uri.Scheme != "socks4a"))
                return "proxyUrl must be an absolute http://, https:// or socks5:// URL.";
            if (!string.IsNullOrEmpty(uri.UserInfo))
                return "proxyUrl must not contain credentials; set proxyUser and proxyPasswordRef instead.";
        }
        if (!string.IsNullOrWhiteSpace(ProxyPasswordRef)
            && !ProxyPasswordRef.StartsWith("keyring:", StringComparison.OrdinalIgnoreCase)
            && !ProxyPasswordRef.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return "proxyPasswordRef must be keyring:service/account or env:VARIABLE — never the password itself.";
        return null;
    }

    /// <summary>
    /// A copy with blank strings turned into <c>null</c>, and a mode word given
    /// as the proxy URL (<c>--proxy-url system</c> / <c>none</c>) turned into
    /// this layer's mode — so it overrides a lower layer's mode, not its URL.
    /// </summary>
    public BowireNetworkSettings Normalised()
    {
        var urlMode = ParseMode(ProxyUrl);
        var mode = urlMode ?? ParseMode(Mode);
        return new()
        {
            Mode = mode is { } m ? ModeName(m) : Blank(Mode),
            ProxyUrl = urlMode is null ? Blank(ProxyUrl) : null,
        NoProxy = Blank(NoProxy),
            ProxyUser = Blank(ProxyUser),
            ProxyPasswordRef = Blank(ProxyPasswordRef),
            CaBundle = Blank(CaBundle),
        };
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// The network settings on disk: <c>network-config.json</c> for everyone and
/// <c>network-config.&lt;workspaceId&gt;.json</c> for a workspace's override,
/// both under the user's Bowire directory. Same layout as the AI config.
/// </summary>
public static class BowireNetworkSettingsStore
{
    private const string GlobalFilename = "network-config.json";

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The path of the layer for <paramref name="workspaceId"/>, or the global one.</summary>
    public static string PathFor(string? workspaceId) =>
        BowireUserContext.GetUserPath(string.IsNullOrWhiteSpace(workspaceId)
            ? GlobalFilename
            : $"network-config.{Sanitise(workspaceId)}.json");

    /// <summary>The layer, or <c>null</c> when there is none (or it cannot be read).</summary>
    public static BowireNetworkSettings? Load(string? workspaceId)
    {
        try
        {
            var path = PathFor(workspaceId);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<BowireNetworkSettings>(File.ReadAllText(path), s_json)?.Normalised();
        }
#pragma warning disable CA1031 // A broken settings file must not take the workbench down; the next save rewrites it.
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>Write a layer; an empty one removes the file.</summary>
    public static void Save(BowireNetworkSettings settings, string? workspaceId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalised = settings.Normalised();
        var error = normalised.Validate();
        if (error is not null) throw new ArgumentException(error, nameof(settings));

        var path = PathFor(workspaceId);
        if (normalised.IsEmpty)
        {
            if (File.Exists(path)) File.Delete(path);
        }
        else
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(normalised, s_json));
        }
        BowireNetworkPolicy.Invalidate();
    }

    /// <summary>Stamp of the layer's file, so a cached resolution notices an edit made outside Bowire.</summary>
    internal static long Stamp(string? workspaceId)
    {
        try
        {
            var info = new FileInfo(PathFor(workspaceId));
            return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0;
        }
#pragma warning disable CA1031
        catch
        {
            return 0;
        }
#pragma warning restore CA1031
    }

    private static string Sanitise(string id)
    {
        var sb = new System.Text.StringBuilder(id.Length);
        foreach (var c in id)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
        }
        return sb.Length == 0 ? "default" : sb.ToString();
    }
}
