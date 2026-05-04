// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// Resolves whether a Bowire protocol plugin should trust a self-signed
/// certificate served from a loopback address. Off by default — production
/// hosts always validate strictly via the OS trust store. The opt-in is
/// designed for the ASP.NET Core dev-certs flow on developer machines and
/// CI containers where <c>dotnet dev-certs https --trust</c> hasn't run.
/// <para>
/// Settings hierarchy (first hit wins):
/// </para>
/// <list type="bullet">
///   <item>
///     <c>Bowire:{PluginId}:TrustLocalhostCert</c> — per-plugin override.
///     Use when one plugin needs different cert handling than the host's
///     global default (e.g. enable for SignalR but stay strict for REST).
///   </item>
///   <item>
///     <c>Bowire:TrustLocalhostCert</c> — global default for every TLS-
///     bearing plugin. Recommended choice for a typical local-dev host.
///   </item>
///   <item>Otherwise — <c>false</c>.</item>
/// </list>
/// <para>
/// Even when the flag is on, the relaxed validation only fires for URLs
/// whose host is <c>localhost</c>, <c>127.0.0.1</c> or <c>::1</c>. A
/// production hostname accidentally seen by a misconfigured Bowire host
/// is still validated against the OS trust store.
/// </para>
/// </summary>
public static class LocalhostCertTrust
{
    /// <summary>
    /// True if <paramref name="url"/> is loopback AND the host has opted
    /// in for either the named plugin or the global default.
    /// </summary>
    /// <param name="config">
    /// Application <see cref="IConfiguration"/>; usually obtained by the
    /// plugin from its <c>Initialize</c> service provider. Null returns
    /// <c>false</c> (standalone / test paths without DI hosting).
    /// </param>
    /// <param name="pluginId">Plugin id like "signalr" / "websocket". Case-sensitive against the config key.</param>
    /// <param name="url">Target URL the plugin is about to open.</param>
    public static bool IsTrustedFor(IConfiguration? config, string pluginId, string url)
    {
        if (config is null) return false;
        if (!IsLocalhostUrl(url)) return false;

        // Plugin-specific override wins. Use the raw section value rather
        // than GetValue<bool> so we can distinguish "explicitly false"
        // from "unset" — an explicit per-plugin false should still beat
        // the global default true.
        var pluginKey = $"Bowire:{pluginId}:TrustLocalhostCert";
        var rawPlugin = config[pluginKey];
        if (!string.IsNullOrWhiteSpace(rawPlugin) && bool.TryParse(rawPlugin, out var pluginVal))
        {
            return pluginVal;
        }

        return config.GetValue<bool>("Bowire:TrustLocalhostCert", false);
    }

    /// <summary>
    /// True when the URL points at <c>localhost</c>, <c>127.0.0.1</c>,
    /// or <c>::1</c>. Defence in depth — every relaxed-validation path
    /// guards on this in addition to the configuration flag.
    /// </summary>
    public static bool IsLocalhostUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var host = u.Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1"
            || host == "::1"
            || host == "[::1]";
    }
}
