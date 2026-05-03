// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Kuestenlogik.Bowire.Protocol.Rest;

/// <summary>
/// Hand-rolled AWS Signature Version 4 signer for outbound HTTP requests.
/// Implements the spec at
/// https://docs.aws.amazon.com/IAM/latest/UserGuide/create-signed-request.html
/// — canonical request → string-to-sign → derived signing key → HMAC.
///
/// Used by <see cref="RestInvoker"/> when the auth-helper pipeline marks a
/// request as needing AWS Sig v4 (via the magic <c>__bowireAwsSigV4__</c>
/// metadata key carrying a JSON credentials blob). Adds the
/// <c>X-Amz-Date</c>, <c>X-Amz-Content-Sha256</c>, optional
/// <c>X-Amz-Security-Token</c>, and <c>Authorization</c> headers in place
/// on the supplied <see cref="HttpRequestMessage"/>.
/// </summary>
internal static class AwsSigV4Signer
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string Aws4Request = "aws4_request";
    private const string EmptyBodyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static async Task SignAsync(
        HttpRequestMessage request,
        string accessKeyId,
        string secretAccessKey,
        string? sessionToken,
        string region,
        string service,
        CancellationToken ct)
    {
        if (request.RequestUri is null)
            throw new InvalidOperationException("AWS Sig v4 requires a fully-qualified RequestUri.");

        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var uri = request.RequestUri;
        var canonicalUri = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        var canonicalQueryString = BuildCanonicalQueryString(uri.Query);

        // Hash the body — empty body has a well-known hash so we don't have
        // to allocate for the most common case.
        string payloadHash;
        if (request.Content is null)
        {
            payloadHash = EmptyBodyHash;
        }
        else
        {
            var bodyBytes = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            payloadHash = bodyBytes.Length == 0
                ? EmptyBodyHash
                : ToHex(SHA256.HashData(bodyBytes));
        }

        // The signed headers must include host, x-amz-date, x-amz-content-sha256,
        // and any caller-supplied headers we want to bind into the signature.
        // We add x-amz-date / x-amz-content-sha256 to the request first so the
        // canonicalisation step picks them up alongside everything else.
        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        request.Headers.TryAddWithoutValidation("X-Amz-Content-Sha256", payloadHash);
        if (!string.IsNullOrEmpty(sessionToken))
            request.Headers.TryAddWithoutValidation("X-Amz-Security-Token", sessionToken);

        var host = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);

        // AWS Sig v4 requires header names lowercased exactly. The CA1308
        // analyzer warns about ToLowerInvariant() because it can be a
        // security smell for security-token comparisons — irrelevant here,
        // since the result feeds the canonical request that the same
        // algorithm later signs.
#pragma warning disable CA1308
        var allHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host
        };
        foreach (var h in request.Headers)
            allHeaders[h.Key.ToLowerInvariant()] = string.Join(",", h.Value).Trim();
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers)
                allHeaders[h.Key.ToLowerInvariant()] = string.Join(",", h.Value).Trim();
        }
#pragma warning restore CA1308

        var canonicalHeadersBuilder = new StringBuilder();
        foreach (var (k, v) in allHeaders)
        {
            canonicalHeadersBuilder.Append(k).Append(':').Append(v).Append('\n');
        }
        var canonicalHeaders = canonicalHeadersBuilder.ToString();
        var signedHeaders = string.Join(";", allHeaders.Keys);

        var canonicalRequest = $"{request.Method.Method}\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        var credentialScope = $"{dateStamp}/{region}/{service}/{Aws4Request}";
        var stringToSign = $"{Algorithm}\n{amzDate}\n{credentialScope}\n{ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

        // Derive the chained signing key
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        var kSigning = HmacSha256(kService, Aws4Request);

        var signature = ToHex(HmacSha256(kSigning, stringToSign));

        var authHeader = $"{Algorithm} Credential={accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);
    }

    private static byte[] HmacSha256(byte[] key, string data)
        => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string ToHex(byte[] bytes)
    {
        // Hex.ToLowerInvariant — AWS Sig v4 wants lowercase. Convert.ToHexString
        // returns uppercase, so post-process. CA1308 is fine here because the
        // result is fed into a cryptographic signature, not a security
        // comparison.
#pragma warning disable CA1308
        return Convert.ToHexString(bytes).ToLowerInvariant();
#pragma warning restore CA1308
    }

    /// <summary>
    /// Sort the query string parameters by name, URL-encode them, and join
    /// them with '&amp;'. AWS Sig v4 requires the canonical query string to
    /// have parameters in code-point order; values stay verbatim if they are
    /// already URL-encoded (the canonical form expects each segment to be
    /// percent-encoded once).
    /// </summary>
    private static string BuildCanonicalQueryString(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?") return string.Empty;
        var q = query.StartsWith('?') ? query[1..] : query;
        if (q.Length == 0) return string.Empty;

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var part in q.Split('&'))
        {
            var idx = part.IndexOf('=', StringComparison.Ordinal);
            var k = idx >= 0 ? part[..idx] : part;
            var v = idx >= 0 ? part[(idx + 1)..] : string.Empty;
            pairs.Add(new(k, v));
        }
        pairs.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        var sb = new StringBuilder();
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i > 0) sb.Append('&');
            sb.Append(pairs[i].Key).Append('=').Append(pairs[i].Value);
        }
        return sb.ToString();
    }
}
