// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// RFC 7616 HTTP Digest authentication (#679): MD5 and SHA-256, their
/// <c>-sess</c> variants, <c>qop=auth</c> and <c>qop=auth-int</c>, <c>userhash</c>,
/// and a retry on a <c>stale</c> nonce.
/// </summary>
/// <remarks>
/// The request goes out once without credentials; a 401 carrying a Digest
/// challenge is answered by sending it again with an <c>Authorization</c>
/// header. The body is buffered first, because it has to be sent twice and,
/// for <c>auth-int</c>, hashed.
/// </remarks>
public sealed class BowireDigestHandler : DelegatingHandler
{
    private readonly string _user;
    private readonly string _password;

    /// <summary>Digest with <paramref name="user"/> and <paramref name="password"/>.</summary>
    public BowireDigestHandler(string user, string password)
    {
        _user = user;
        _password = password;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[]? body = null;
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < 2 && response.StatusCode == HttpStatusCode.Unauthorized; attempt++)
        {
            var challenge = DigestChallenge.Select(response.Headers.WwwAuthenticate);
            // A second 401 is only worth answering when the server says the
            // nonce went stale; otherwise the credentials are simply wrong.
            if (challenge is null || (attempt == 1 && !challenge.Stale)) break;

            // The retry stays referenced by the response it produces
            // (HttpResponseMessage.RequestMessage), as the original does.
#pragma warning disable CA2000
            var retry = Clone(request, body);
#pragma warning restore CA2000
            var target = request.RequestUri!.PathAndQuery;
            retry.Headers.Authorization = new AuthenticationHeaderValue(
                "Digest", Authorize(challenge, request.Method.Method, target, body, _user, _password, NewCnonce(), 1));
            response.Dispose();
            response = await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
        }
        return response;
    }

    /// <summary>The parameter string of an <c>Authorization: Digest …</c> header.</summary>
    internal static string Authorize(
        DigestChallenge challenge, string method, string uri, byte[]? body,
        string user, string password, string cnonce, int nonceCount)
    {
        var algorithm = challenge.Algorithm;
        var hash = algorithm.StartsWith("SHA-256", StringComparison.OrdinalIgnoreCase)
            ? (Func<string, string>)Sha256
            : Md5;
        var sess = algorithm.EndsWith("-sess", StringComparison.OrdinalIgnoreCase);
        var nc = nonceCount.ToString("x8", CultureInfo.InvariantCulture);

        var ha1 = hash($"{user}:{challenge.Realm}:{password}");
        if (sess) ha1 = hash($"{ha1}:{challenge.Nonce}:{cnonce}");

        var qop = challenge.Qop;
        var ha2 = qop == "auth-int"
            ? hash($"{method}:{uri}:{HashBytes(algorithm, body ?? [])}")
            : hash($"{method}:{uri}");

        var response = qop is null
            ? hash($"{ha1}:{challenge.Nonce}:{ha2}")
            : hash($"{ha1}:{challenge.Nonce}:{nc}:{cnonce}:{qop}:{ha2}");

        var username = challenge.UserHash ? hash($"{user}:{challenge.Realm}") : user;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"username=\"{Escape(username)}\", realm=\"{Escape(challenge.Realm)}\", nonce=\"{Escape(challenge.Nonce)}\", uri=\"{Escape(uri)}\"");
        if (challenge.AlgorithmGiven) sb.Append(CultureInfo.InvariantCulture, $", algorithm={algorithm}");
        sb.Append(CultureInfo.InvariantCulture, $", response=\"{response}\"");
        if (challenge.Opaque is not null) sb.Append(CultureInfo.InvariantCulture, $", opaque=\"{Escape(challenge.Opaque)}\"");
        if (qop is not null) sb.Append(CultureInfo.InvariantCulture, $", qop={qop}, nc={nc}, cnonce=\"{Escape(cnonce)}\"");
        if (challenge.UserHash) sb.Append(", userhash=true");
        return sb.ToString();
    }

    private static string NewCnonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToUpperInvariant();

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

#pragma warning disable CA5351 // MD5 is what the Digest scheme specifies; the server picks it, not Bowire.
    private static string Md5(string text) => Hex(MD5.HashData(Encoding.UTF8.GetBytes(text)));
#pragma warning restore CA5351

    private static string Sha256(string text) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

#pragma warning disable CA5351
    private static string HashBytes(string algorithm, byte[] bytes) =>
        Hex(algorithm.StartsWith("SHA-256", StringComparison.OrdinalIgnoreCase) ? SHA256.HashData(bytes) : MD5.HashData(bytes));
#pragma warning restore CA5351

#pragma warning disable CA1308 // Digest hex is lower-case by definition (RFC 7616 section 3.4.1).
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
#pragma warning restore CA1308

    private static HttpRequestMessage Clone(HttpRequestMessage original, byte[]? body)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
        };
        foreach (var header in original.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        foreach (var option in original.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }
        if (body is not null && original.Content is not null)
        {
            var content = new ByteArrayContent(body);
            foreach (var header in original.Content.Headers)
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            clone.Content = content;
        }
        return clone;
    }
}

/// <summary>One parsed <c>WWW-Authenticate: Digest …</c> challenge.</summary>
internal sealed class DigestChallenge
{
    public required string Realm { get; init; }
    public required string Nonce { get; init; }
    public string? Opaque { get; init; }
    public required string Algorithm { get; init; }
    public bool AlgorithmGiven { get; init; }
    public string? Qop { get; init; }
    public bool Stale { get; init; }
    public bool UserHash { get; init; }

    /// <summary>
    /// The challenge to answer out of those offered: SHA-256 before MD5, and a
    /// usable one at all — an algorithm Bowire does not implement is skipped.
    /// </summary>
    public static DigestChallenge? Select(HttpHeaderValueCollection<AuthenticationHeaderValue> headers)
    {
        DigestChallenge? best = null;
        foreach (var header in headers)
        {
            if (!string.Equals(header.Scheme, "Digest", StringComparison.OrdinalIgnoreCase)) continue;
            var challenge = Parse(header.Parameter);
            if (challenge is null) continue;
            if (best is null || (challenge.Algorithm.StartsWith("SHA-256", StringComparison.OrdinalIgnoreCase)
                                 && !best.Algorithm.StartsWith("SHA-256", StringComparison.OrdinalIgnoreCase)))
                best = challenge;
        }
        return best;
    }

    public static DigestChallenge? Parse(string? parameters)
    {
        if (string.IsNullOrEmpty(parameters)) return null;
        var values = ParseParameters(parameters);
        if (!values.TryGetValue("realm", out var realm) || !values.TryGetValue("nonce", out var nonce)) return null;

        var algorithmGiven = values.TryGetValue("algorithm", out var algorithm);
        algorithm ??= "MD5";
        var known = algorithm.ToUpperInvariant() is "MD5" or "MD5-SESS" or "SHA-256" or "SHA-256-SESS";
        if (!known) return null;

        // Prefer auth: it does not need the body hashed. auth-int when that is all there is.
        string? qop = null;
        if (values.TryGetValue("qop", out var qopList))
        {
            var offered = qopList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            qop = offered.Contains("auth", StringComparer.OrdinalIgnoreCase) ? "auth"
                : offered.Contains("auth-int", StringComparer.OrdinalIgnoreCase) ? "auth-int"
                : null;
            if (qop is null) return null;
        }

        return new DigestChallenge
        {
            Realm = realm,
            Nonce = nonce,
            Opaque = values.GetValueOrDefault("opaque"),
            Algorithm = algorithm,
            AlgorithmGiven = algorithmGiven,
            Qop = qop,
            Stale = string.Equals(values.GetValueOrDefault("stale"), "true", StringComparison.OrdinalIgnoreCase),
            UserHash = string.Equals(values.GetValueOrDefault("userhash"), "true", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary><c>key=value</c> and <c>key="quoted, value"</c> pairs.</summary>
    internal static Dictionary<string, string> ParseParameters(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (text[i] == ',' || char.IsWhiteSpace(text[i]))) i++;
            var keyStart = i;
            while (i < text.Length && text[i] != '=' && text[i] != ',') i++;
            var key = text[keyStart..i].Trim();
            if (i >= text.Length || text[i] != '=')
            {
                continue;
            }
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            string value;
            if (i < text.Length && text[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i++;
                    sb.Append(text[i]);
                    i++;
                }
                i++;
                value = sb.ToString();
            }
            else
            {
                var valueStart = i;
                while (i < text.Length && text[i] != ',') i++;
                value = text[valueStart..i].Trim();
            }
            if (key.Length > 0) result[key] = value;
        }
        return result;
    }
}
