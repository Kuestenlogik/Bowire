// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>The HTTP authentication schemes that need a challenge round-trip (#679).</summary>
public enum BowireHttpAuthScheme
{
    /// <summary>Windows-integrated: Kerberos, falling back to NTLM, with the signed-in user's credentials.</summary>
    Negotiate,

    /// <summary>NTLM (via Negotiate where offered) with explicit domain, user and password.</summary>
    Ntlm,

    /// <summary>RFC 7616 Digest, including <c>qop=auth-int</c>.</summary>
    Digest,
}

/// <summary>One call's challenge-response credentials. Never serialised back out.</summary>
public sealed class BowireHttpAuthConfig
{
    internal BowireHttpAuthConfig(BowireHttpAuthScheme scheme, string? user, string? password, string? domain)
    {
        Scheme = scheme;
        User = user;
        Password = password;
        Domain = domain;
    }

    /// <summary>The scheme.</summary>
    public BowireHttpAuthScheme Scheme { get; }

    /// <summary>The user name, without the domain.</summary>
    public string? User { get; }

    internal string? Password { get; }

    /// <summary>The Windows domain, for NTLM.</summary>
    public string? Domain { get; }

    /// <summary>
    /// Identifies the credential set without holding the password, so
    /// connections authenticated as one user are never reused for another.
    /// </summary>
    internal string CacheKey
    {
        get
        {
            var secret = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Password ?? string.Empty)));
            return $"{Scheme}|{Domain}|{User}|{secret}";
        }
    }
}

/// <summary>
/// Negotiate / NTLM / Digest for every protocol that goes over HTTP (#679).
/// </summary>
/// <remarks>
/// <para>
/// The workbench sends the scheme and the resolved credentials as the
/// <see cref="MarkerKey"/> metadata entry, the same way mTLS and SigV4 travel.
/// A plugin hands its request and metadata to <see cref="ApplyMetadata"/>;
/// the credentials ride on the request's options, and the client from
/// <c>BowireHttpClientFactory.Create</c> answers the challenge with a handler
/// kept per credential set. Connection-based schemes (NTLM, Negotiate)
/// authenticate the TCP connection, so a connection authenticated as one
/// user must never carry another user's request — hence one handler each.
/// </para>
/// <para>
/// .NET answers Negotiate and NTLM challenges itself. Digest it does too, but
/// only <c>qop=auth</c>; <see cref="BowireDigestHandler"/> implements RFC 7616
/// in full, <c>auth-int</c> included.
/// </para>
/// </remarks>
public static class BowireHttpAuth
{
    /// <summary>The metadata key the workbench, the CLI and MCP put the credentials under.</summary>
    public const string MarkerKey = "__bowireHttpAuth__";

    /// <summary>Where <see cref="ApplyMetadata"/> leaves the credentials on a request.</summary>
    public static readonly HttpRequestOptionsKey<BowireHttpAuthConfig> OptionKey = new("Kuestenlogik.Bowire.HttpAuth");

    /// <summary>
    /// Whether Windows-integrated authentication (ambient credentials) can work
    /// here. Only a Windows host has a signed-in user's ticket to offer.
    /// </summary>
    public static bool IntegratedAvailable => OperatingSystem.IsWindows();

    /// <summary>The schemes this host can use, for the workbench to offer.</summary>
    public static IReadOnlyList<(string Id, bool Available, string? Reason)> Capabilities =>
    [
        ("negotiate", IntegratedAvailable, IntegratedAvailable ? null : "Windows-integrated authentication needs Bowire to run on Windows."),
        ("ntlm", true, null),
        ("digest", true, null),
    ];

    /// <summary>The credentials in <paramref name="metadata"/>, or <c>null</c>.</summary>
    public static BowireHttpAuthConfig? TryParse(IEnumerable<KeyValuePair<string, string>>? metadata)
    {
        if (metadata is null) return null;
        foreach (var (key, value) in metadata)
        {
            if (!string.Equals(key, MarkerKey, StringComparison.Ordinal)) continue;
            return Parse(value);
        }
        return null;
    }

    /// <summary>Parse the marker's JSON value; <c>null</c> when it is not usable.</summary>
    public static BowireHttpAuthConfig? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var scheme = Read(root, "scheme")?.ToUpperInvariant() switch
            {
                "NEGOTIATE" or "KERBEROS" or "WINDOWS" => BowireHttpAuthScheme.Negotiate,
                "NTLM" => BowireHttpAuthScheme.Ntlm,
                "DIGEST" => BowireHttpAuthScheme.Digest,
                _ => (BowireHttpAuthScheme?)null,
            };
            if (scheme is null) return null;

            var user = Read(root, "user") ?? Read(root, "username");
            var domain = Read(root, "domain");
            // DOMAIN\user is how Windows users write it.
            if (user is not null && domain is null && user.IndexOf('\\', StringComparison.Ordinal) is var slash and > 0)
            {
                domain = user[..slash];
                user = user[(slash + 1)..];
            }
            if (scheme != BowireHttpAuthScheme.Negotiate && string.IsNullOrEmpty(user)) return null;
            return new BowireHttpAuthConfig(scheme.Value, user, Read(root, "password"), domain);
        }
        catch (JsonException)
        {
            return null;
        }

        static string? Read(JsonElement root, string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
    }

    /// <summary>
    /// Put <paramref name="metadata"/> on <paramref name="request"/>: the real
    /// headers as headers, the credentials on the request's options. Bowire's
    /// internal markers never become headers. <paramref name="exclude"/> names
    /// keys that are plugin settings rather than headers (SOAP's
    /// <c>soap_action</c>, for instance).
    /// </summary>
    public static void ApplyMetadata(
        HttpRequestMessage request,
        IEnumerable<KeyValuePair<string, string>>? metadata,
        Func<string, bool>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (metadata is null) return;
        foreach (var (key, value) in BowireMetadataKeys.WireHeaders(metadata))
        {
            if (exclude is not null && exclude(key)) continue;
            request.Headers.TryAddWithoutValidation(key, value);
        }
        Attach(request, metadata);
    }

    /// <summary>Carry the credentials in <paramref name="metadata"/>, if any, on <paramref name="request"/>.</summary>
    public static void Attach(HttpRequestMessage request, IEnumerable<KeyValuePair<string, string>>? metadata)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (TryParse(metadata) is { } config) request.Options.Set(OptionKey, config);
    }

    /// <summary>Set up <paramref name="handler"/> to answer the challenge of <paramref name="config"/>.</summary>
    public static void ApplyTo(HttpClientHandler handler, BowireHttpAuthConfig config)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(config);
        EnsureAvailable(config);
        handler.PreAuthenticate = false;
        switch (config.Scheme)
        {
            case BowireHttpAuthScheme.Negotiate:
                handler.UseDefaultCredentials = true;
                break;
            case BowireHttpAuthScheme.Ntlm:
                handler.Credentials = new SchemeCredentials(ToNetworkCredential(config), "NTLM", "Negotiate");
                break;
            default:
                // Digest is answered by BowireDigestHandler, never by the platform:
                // no credentials here, so .NET cannot fall back to Basic.
                handler.Credentials = null;
                break;
        }
    }

    /// <summary>Set up a WebSocket handshake to answer the challenge of <paramref name="config"/>.</summary>
    /// <remarks>Digest on a WebSocket upgrade goes through the platform, which supports <c>qop=auth</c> only.</remarks>
    public static void ApplyTo(ClientWebSocketOptions options, BowireHttpAuthConfig config)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(config);
        EnsureAvailable(config);
        switch (config.Scheme)
        {
            case BowireHttpAuthScheme.Negotiate:
                options.UseDefaultCredentials = true;
                break;
            case BowireHttpAuthScheme.Ntlm:
                options.Credentials = new SchemeCredentials(ToNetworkCredential(config), "NTLM", "Negotiate");
                break;
            default:
                options.Credentials = new SchemeCredentials(ToNetworkCredential(config), "Digest");
                break;
        }
    }

    /// <summary>
    /// <paramref name="inner"/> wrapped for <paramref name="config"/>: a Digest
    /// handler for Digest, the handler itself otherwise.
    /// </summary>
    public static HttpMessageHandler Wrap(HttpMessageHandler inner, BowireHttpAuthConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Scheme == BowireHttpAuthScheme.Digest
            ? new BowireDigestHandler(config.User ?? string.Empty, config.Password ?? string.Empty) { InnerHandler = inner }
            : inner;
    }

    private static void EnsureAvailable(BowireHttpAuthConfig config)
    {
        if (config.Scheme == BowireHttpAuthScheme.Negotiate && !IntegratedAvailable)
            throw new PlatformNotSupportedException("Windows-integrated authentication needs Bowire to run on Windows. Use NTLM with explicit credentials instead.");
    }

    private static NetworkCredential ToNetworkCredential(BowireHttpAuthConfig config) =>
        config.Domain is null
            ? new NetworkCredential(config.User, config.Password)
            : new NetworkCredential(config.User, config.Password, config.Domain);

    /// <summary>
    /// Credentials offered only to the named schemes, so a server that
    /// answers with a Basic challenge never receives the password in the clear.
    /// </summary>
    private sealed class SchemeCredentials(NetworkCredential credential, params string[] schemes) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType) =>
            schemes.Any(s => string.Equals(s, authType, StringComparison.OrdinalIgnoreCase)) ? credential : null;
    }
}
