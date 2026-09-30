// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Net;

/// <summary>
/// How Bowire's outbound connections leave the machine (#680): through which
/// proxy, past which hosts directly, and trusting which extra certificate
/// authorities.
/// </summary>
/// <remarks>
/// <para>
/// Settings come in layers, later ones winning field by field: the built-in
/// default (<c>system</c>), the global file, the active workspace's override,
/// and the host configuration (<c>Bowire:Network:*</c> from appsettings,
/// <c>BOWIRE_Bowire__Network__*</c> or the <c>--proxy-url</c> / <c>--no-proxy</c> /
/// <c>--ca-bundle</c> flags). The host configuration wins because it is the
/// operator's: a CI job that passes <c>--proxy-url</c> means it.
/// </para>
/// <para>
/// <see cref="Proxy"/> is one <see cref="IWebProxy"/> that re-resolves on every
/// request. A plugin builds its HttpClient once, at startup; the workspace is
/// only known per call. Asking the proxy object per request is what lets one
/// long-lived client follow whichever workspace the call came from. The
/// workspace is the one <see cref="BowirePluginSettingsScope"/> names, which
/// every Bowire endpoint enters from its <c>workspaceId</c> parameter.
/// </para>
/// <para>
/// Loopback targets always go direct. A local dev server behind the corporate
/// proxy is never what anyone means, and the proxy could not reach it anyway.
/// </para>
/// </remarks>
public static class BowireNetworkPolicy
{
    private static readonly ConcurrentDictionary<string, BowireEffectiveNetwork> s_cache = new(StringComparer.Ordinal);
    private static IConfiguration? s_configuration;
    private static IWebProxy? s_systemProxy;
    private static Func<string, string?>? s_keyringLookup;
    private static int s_generation;

    /// <summary>The proxy every Bowire-built handler uses; resolves per request.</summary>
    public static IWebProxy Proxy { get; } = new PolicyProxy();

    /// <summary>
    /// Hand the policy the host configuration. The workbench does this when it
    /// maps its endpoints, the CLI at startup. Without it only the files apply.
    /// </summary>
    public static void UseConfiguration(IConfiguration? configuration)
    {
        s_configuration = configuration;
        Invalidate();
    }

    /// <summary>
    /// How <c>keyring:service/account</c> password references resolve. Set by
    /// the keyring module when it is loaded; without it only <c>env:</c> works.
    /// </summary>
    public static void UseKeyring(Func<string, string?>? lookup)
    {
        s_keyringLookup = lookup;
        Invalidate();
    }

    /// <summary>
    /// Make <see cref="Proxy"/> the process-wide <see cref="HttpClient.DefaultProxy"/>,
    /// so connections Bowire does not build itself — plugin downloads, the
    /// update check, catalogue providers, third-party client libraries — follow
    /// the same settings.
    /// </summary>
    /// <remarks>
    /// Only the standalone tool calls this. Embedded in someone else's ASP.NET
    /// app, Bowire must not change how the host's own HttpClients connect, so
    /// there the policy reaches only the handlers Bowire builds.
    /// </remarks>
    public static void InstallAsProcessDefault()
    {
        CaptureSystemProxy();
        if (!ReferenceEquals(HttpClient.DefaultProxy, Proxy)) HttpClient.DefaultProxy = Proxy;
    }

    /// <summary>Forget cached resolutions; called when a settings layer changes.</summary>
    public static void Invalidate()
    {
        Interlocked.Increment(ref s_generation);
        s_cache.Clear();
    }

    /// <summary>The settings in force for the current call's workspace.</summary>
    public static BowireEffectiveNetwork Current => For(BowirePluginSettingsScope.Current?.WorkspaceId);

    /// <summary>The settings in force for <paramref name="workspaceId"/> (<c>null</c>: global only).</summary>
    public static BowireEffectiveNetwork For(string? workspaceId)
    {
        // Keyed by the files, not the id: in a multi-user host each user's
        // settings live under their own directory.
        var key = BowireNetworkSettingsStore.PathFor(null) + "|" + (workspaceId is null ? string.Empty : BowireNetworkSettingsStore.PathFor(workspaceId));
        var stamp = HashCode.Combine(
            Volatile.Read(ref s_generation),
            BowireNetworkSettingsStore.Stamp(null),
            workspaceId is null ? 0 : BowireNetworkSettingsStore.Stamp(workspaceId));
        if (s_cache.TryGetValue(key, out var cached) && cached.Stamp == stamp) return cached;

        var resolved = Resolve(workspaceId, stamp);
        s_cache[key] = resolved;
        return resolved;
    }

    /// <summary>The layers for <paramref name="workspaceId"/>, for the settings UI.</summary>
    public static (BowireNetworkSettings? Global, BowireNetworkSettings? Workspace, BowireNetworkSettings Host) Layers(string? workspaceId) =>
        (BowireNetworkSettingsStore.Load(null),
         workspaceId is null ? null : BowireNetworkSettingsStore.Load(workspaceId),
         HostLayer());

    /// <summary>
    /// Route <paramref name="handler"/> through the policy: the per-request
    /// proxy, and the CA bundle on top of whatever validation it already has.
    /// Call it after the handler's own certificate callback is set.
    /// </summary>
    public static HttpClientHandler Apply(HttpClientHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handler.Proxy = Proxy;
        handler.UseProxy = true;
        var previous = handler.ServerCertificateCustomValidationCallback;
#pragma warning disable CA5359 // Strict unless the inner callback or the configured CA bundle accepts.
        handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            (previous is null ? errors == SslPolicyErrors.None : previous(request, cert, chain, errors))
            || TrustsCustomCa(cert, chain, errors);
#pragma warning restore CA5359
        return handler;
    }

    /// <inheritdoc cref="Apply(HttpClientHandler)"/>
    public static SocketsHttpHandler Apply(SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handler.Proxy = Proxy;
        handler.UseProxy = true;
        var previous = handler.SslOptions.RemoteCertificateValidationCallback;
        handler.SslOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
            (previous is null ? errors == SslPolicyErrors.None : previous(sender, cert, chain, errors))
            || TrustsCustomCa(cert, chain, errors);
        return handler;
    }

    /// <inheritdoc cref="Apply(HttpClientHandler)"/>
    public static ClientWebSocketOptions Apply(ClientWebSocketOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Proxy = Proxy;
        var previous = options.RemoteCertificateValidationCallback;
        options.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
            (previous is null ? errors == SslPolicyErrors.None : previous(sender, cert, chain, errors))
            || TrustsCustomCa(cert, chain, errors);
        return options;
    }

    /// <summary>
    /// Whether the configured CA bundle vouches for a certificate the system
    /// store rejected. Only a chain problem can be rescued; a wrong host name
    /// or a missing certificate stays a failure whatever the bundle says.
    /// </summary>
    public static bool TrustsCustomCa(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || certificate is null) return false;
        var roots = Current.CaCertificates;
        if (roots is null || roots.Count == 0) return false;

        var leaf = certificate as X509Certificate2;
        var owned = leaf is null;
#pragma warning disable CA2000 // Disposed below when this method created it.
        leaf ??= X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
#pragma warning restore CA2000
        try
        {
            using var custom = new X509Chain();
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.CustomTrustStore.AddRange(roots);
            custom.ChainPolicy.ExtraStore.AddRange(roots);
            // A corporate MITM proxy's CA rarely publishes a CRL Bowire could reach.
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (chain is not null)
            {
                foreach (var element in chain.ChainElements) custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
            return custom.Build(leaf);
        }
        finally
        {
            if (owned) leaf.Dispose();
        }
    }

    private static void CaptureSystemProxy()
    {
        // HttpClient.DefaultProxy is the OS / environment proxy until someone
        // replaces it — capture it before InstallAsProcessDefault does.
        if (s_systemProxy is null)
        {
            var current = HttpClient.DefaultProxy;
            if (!ReferenceEquals(current, Proxy)) s_systemProxy = current;
        }
    }

    private static BowireNetworkSettings HostLayer()
    {
        var section = s_configuration?.GetSection("Bowire:Network");
        if (section is null) return new BowireNetworkSettings();
        return new BowireNetworkSettings
        {
            Mode = section["Mode"],
            ProxyUrl = section["ProxyUrl"],
            NoProxy = section["NoProxy"],
            ProxyUser = section["ProxyUser"],
            ProxyPasswordRef = section["ProxyPasswordRef"],
            CaBundle = section["CaBundle"],
        }.Normalised();
    }

    private static BowireEffectiveNetwork Resolve(string? workspaceId, int stamp)
    {
        CaptureSystemProxy();
        var (global, workspace, host) = Layers(workspaceId);
        var merged = new BowireNetworkSettings();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (layer, name) in new[] { (global, "global"), (workspace, "workspace"), (host, "host") })
        {
            if (layer is null) continue;
            Take(layer.Mode, v => merged.Mode = v, "mode");
            Take(layer.ProxyUrl, v => merged.ProxyUrl = v, "proxyUrl");
            Take(layer.NoProxy, v => merged.NoProxy = v, "noProxy");
            Take(layer.ProxyUser, v => merged.ProxyUser = v, "proxyUser");
            Take(layer.ProxyPasswordRef, v => merged.ProxyPasswordRef = v, "proxyPasswordRef");
            Take(layer.CaBundle, v => merged.CaBundle = v, "caBundle");

            void Take(string? value, Action<string> set, string field)
            {
                if (value is null) return;
                set(value);
                sources[field] = name;
            }
        }

        var problems = new List<string>();
        var validation = merged.Validate();
        if (validation is not null) problems.Add(validation);

        // A proxy URL without a mode means manual. (A mode word given as the URL
        // was already turned into its layer's mode by Normalised.)
        var mode = BowireNetworkSettings.ParseMode(merged.Mode);
        Uri? proxyUri = null;
        if (merged.ProxyUrl is { } url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && string.IsNullOrEmpty(parsed.UserInfo)) proxyUri = parsed;
            mode ??= BowireProxyMode.Manual;
        }
        mode ??= BowireProxyMode.System;
        if (mode == BowireProxyMode.Manual && proxyUri is null)
            problems.Add("manual mode needs a proxy URL; connecting directly until one is set.");

        NetworkCredential? credential = null;
        if (merged.ProxyUser is { } user)
        {
            var password = ResolvePassword(merged.ProxyPasswordRef, problems);
            var slash = user.IndexOf('\\', StringComparison.Ordinal);
            credential = slash > 0
                ? new NetworkCredential(user[(slash + 1)..], password, user[..slash])
                : new NetworkCredential(user, password);
        }

        var (cas, caError) = LoadCaBundle(merged.CaBundle);
        if (caError is not null) problems.Add(caError);

        return new BowireEffectiveNetwork(
            stamp, mode.Value, proxyUri, BowireNoProxyList.Parse(merged.NoProxy),
            credential, cas, merged, sources, problems, s_systemProxy);
    }

    private static string? ResolvePassword(string? reference, List<string> problems)
    {
        if (reference is null) return null;
        if (reference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var value = Environment.GetEnvironmentVariable(reference[4..]);
            if (value is null) problems.Add($"proxy password: environment variable {reference[4..]} is not set.");
            return value;
        }
        if (reference.StartsWith("keyring:", StringComparison.OrdinalIgnoreCase))
        {
            if (s_keyringLookup is null)
            {
                problems.Add("proxy password: the keyring module is not loaded here; use env:VARIABLE.");
                return null;
            }
            var value = s_keyringLookup(reference[8..]);
            if (value is null) problems.Add($"proxy password: nothing in the keyring under {reference[8..]}.");
            return value;
        }
        return null;
    }

    private static (X509Certificate2Collection? Certificates, string? Error) LoadCaBundle(string? bundle)
    {
        if (bundle is null) return (null, null);
        string pem;
        if (bundle.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            pem = bundle;
        }
        else
        {
            var path = Environment.ExpandEnvironmentVariables(bundle);
            if (!File.Exists(path)) return (null, $"CA bundle {path} does not exist.");
            try
            {
                pem = File.ReadAllText(path);
            }
#pragma warning disable CA1031 // Reported in the settings, not thrown at a connection.
            catch (Exception ex)
            {
                return (null, $"CA bundle {path} cannot be read: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        var certificates = new X509Certificate2Collection();
        try
        {
            certificates.ImportFromPem(pem);
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            return (null, $"CA bundle is not valid PEM: {ex.Message}");
        }
#pragma warning restore CA1031
        return certificates.Count == 0
            ? (null, "CA bundle contains no certificate.")
            : (certificates, null);
    }

    private sealed class PolicyProxy : IWebProxy
    {
        public ICredentials? Credentials
        {
            get => PolicyCredentials.Instance;
            set { /* Credentials come from the policy; a caller cannot replace them. */ }
        }

        public Uri? GetProxy(Uri destination) => Current.ProxyFor(destination);

        public bool IsBypassed(Uri host) => Current.ProxyFor(host) is null;
    }

    private sealed class PolicyCredentials : ICredentials
    {
        public static readonly PolicyCredentials Instance = new();

        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            var current = Current;
            return current.Mode == BowireProxyMode.System
                ? current.SystemProxy?.Credentials?.GetCredential(uri, authType)
                : current.Credential;
        }
    }
}

/// <summary>The network settings in force for one workspace, resolved (#680).</summary>
public sealed class BowireEffectiveNetwork
{
    internal BowireEffectiveNetwork(
        int stamp, BowireProxyMode mode, Uri? proxyUri, BowireNoProxyList bypass,
        NetworkCredential? credential, X509Certificate2Collection? caCertificates,
        BowireNetworkSettings merged, IReadOnlyDictionary<string, string> sources,
        IReadOnlyList<string> problems, IWebProxy? systemProxy)
    {
        Stamp = stamp;
        Mode = mode;
        ProxyUri = proxyUri;
        Bypass = bypass;
        Credential = credential;
        CaCertificates = caCertificates;
        Settings = merged;
        Sources = sources;
        Problems = problems;
        SystemProxy = systemProxy;
    }

    internal int Stamp { get; }

    /// <summary>The mode in force.</summary>
    public BowireProxyMode Mode { get; }

    /// <summary>The manual proxy, when <see cref="Mode"/> is manual and one is set.</summary>
    public Uri? ProxyUri { get; }

    /// <summary>Hosts that go direct, on top of loopback.</summary>
    public BowireNoProxyList Bypass { get; }

    /// <summary>The proxy credential. Never serialised.</summary>
    internal NetworkCredential? Credential { get; }

    /// <summary>Whether a proxy credential is configured (without revealing it).</summary>
    public bool HasCredential => Credential is not null;

    /// <summary>The extra trusted CAs, if a bundle is configured and loads.</summary>
    public X509Certificate2Collection? CaCertificates { get; }

    /// <summary>The merged settings; holds references, never secrets.</summary>
    public BowireNetworkSettings Settings { get; }

    /// <summary>Which layer set each field: <c>global</c>, <c>workspace</c> or <c>host</c>.</summary>
    public IReadOnlyDictionary<string, string> Sources { get; }

    /// <summary>What does not work as configured, in words.</summary>
    public IReadOnlyList<string> Problems { get; }

    internal IWebProxy? SystemProxy { get; }

    /// <summary>
    /// The proxy a connection to <paramref name="target"/> goes through, or
    /// <c>null</c> for a direct connection.
    /// </summary>
    public Uri? ProxyFor(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsAbsoluteUri || target.IsLoopback || Bypass.Matches(target)) return null;

        // Proxies speak HTTP; a ws:// / wss:// target tunnels the same way an
        // http:// / https:// one does, so resolve it under that scheme.
        var httpTarget = target.Scheme switch
        {
            "ws" => new UriBuilder(target) { Scheme = Uri.UriSchemeHttp, Port = target.Port }.Uri,
            "wss" => new UriBuilder(target) { Scheme = Uri.UriSchemeHttps, Port = target.Port }.Uri,
            _ => target,
        };

        switch (Mode)
        {
            case BowireProxyMode.None:
                return null;
            case BowireProxyMode.Manual:
                return ProxyUri;
            default:
                var system = SystemProxy;
                if (system is null || system.IsBypassed(httpTarget)) return null;
                var proxy = system.GetProxy(httpTarget);
                // The OS proxy answers "direct" with the destination itself.
                return proxy is null || proxy == httpTarget ? null : proxy;
        }
    }
}
