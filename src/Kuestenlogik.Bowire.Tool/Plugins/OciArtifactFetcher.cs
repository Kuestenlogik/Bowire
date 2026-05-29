// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Kuestenlogik.Bowire.App.Plugins;

/// <summary>
/// Pulls a sidecar-plugin artifact (a single-layer zip) from an OCI
/// registry via the OCI Distribution v2 API — so
/// <c>bowire plugin install --file oci://ghcr.io/acme/zenoh-sidecar:1.0.0</c>
/// works the same way the http(s) / local-file zip paths do.
/// </summary>
/// <remarks>
/// Flow: resolve the manifest (<c>GET /v2/&lt;repo&gt;/manifests/&lt;ref&gt;</c>),
/// following a multi-arch index to its first manifest if needed; pick the
/// artifact layer (a zip-ish mediaType, else the first non-config layer);
/// download that blob (<c>GET /v2/&lt;repo&gt;/blobs/&lt;digest&gt;</c>).
/// Anonymous pulls plus the standard Docker/GHCR bearer-token dance
/// (401 → <c>WWW-Authenticate: Bearer realm=…</c> → fetch token → retry)
/// are handled. Push, cosign signatures, and credential helpers are out
/// of scope.
/// </remarks>
internal static class OciArtifactFetcher
{
    private const string ManifestAccept =
        "application/vnd.oci.image.manifest.v1+json,"
        + "application/vnd.docker.distribution.manifest.v2+json,"
        + "application/vnd.oci.image.index.v1+json,"
        + "application/vnd.docker.distribution.manifest.list.v2+json";

    /// <summary>Fetch the artifact's zip layer to <paramref name="destPath"/>.</summary>
    public static async Task FetchToFileAsync(string ociRef, string destPath, CancellationToken ct)
    {
        var (registry, repo, reference) = ParseReference(ociRef);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // Bearer token is cached across the manifest + blob requests so a
        // single 401 dance covers the whole pull. Held in a one-field box
        // the auth helper can read and refresh.
        var auth = new TokenBox();

        var manifestUri = new Uri($"https://{registry}/v2/{repo}/manifests/{reference}");
        var manifestJson = await GetStringWithAuthAsync(http, manifestUri, ManifestAccept, repo, auth, ct)
            .ConfigureAwait(false);

        // Multi-arch index → follow the first manifest entry.
        var digest = SelectManifestFromIndex(manifestJson);
        if (digest is not null)
        {
            var inner = new Uri($"https://{registry}/v2/{repo}/manifests/{digest}");
            manifestJson = await GetStringWithAuthAsync(http, inner, ManifestAccept, repo, auth, ct)
                .ConfigureAwait(false);
        }

        var layerDigest = SelectLayerDigest(manifestJson)
            ?? throw new InvalidOperationException("OCI artifact has no usable layer (expected a single zip layer).");

        var blobUri = new Uri($"https://{registry}/v2/{repo}/blobs/{layerDigest}");
        using var req = BuildRequest(HttpMethod.Get, blobUri, accept: null, auth.Token);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(destPath);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    private sealed class TokenBox { public string? Token; }

    // ---- pure, unit-testable parsers --------------------------------

    /// <summary>
    /// Parse <c>oci://registry/namespace/repo:tag</c> (or <c>@sha256:…</c>)
    /// into <c>(registry, repo, reference)</c>. <c>oci://</c> prefix
    /// optional; tag defaults to <c>latest</c>.
    /// </summary>
    public static (string Registry, string Repo, string Reference) ParseReference(string ociRef)
    {
        if (string.IsNullOrWhiteSpace(ociRef))
            throw new ArgumentException("Empty OCI reference.", nameof(ociRef));

        var s = ociRef.Trim();
        if (s.StartsWith("oci://", StringComparison.OrdinalIgnoreCase))
            s = s["oci://".Length..];

        // Split off a digest (@sha256:…) or tag (:1.0.0). A digest is
        // unambiguous; for a tag we must not confuse the registry port
        // colon (host:5000/...) with the tag colon.
        string reference;
        var atIdx = s.IndexOf('@', StringComparison.Ordinal);
        if (atIdx >= 0)
        {
            reference = s[(atIdx + 1)..];
            s = s[..atIdx];
        }
        else
        {
            // Tag colon is after the last '/', if any.
            var lastSlash = s.LastIndexOf('/');
            var tagColon = s.IndexOf(':', lastSlash + 1);
            if (tagColon >= 0)
            {
                reference = s[(tagColon + 1)..];
                s = s[..tagColon];
            }
            else
            {
                reference = "latest";
            }
        }

        var firstSlash = s.IndexOf('/', StringComparison.Ordinal);
        if (firstSlash < 0)
            throw new ArgumentException($"OCI reference '{ociRef}' is missing a repository path.", nameof(ociRef));

        var registry = s[..firstSlash];
        var repo = s[(firstSlash + 1)..];
        if (registry.Length == 0 || repo.Length == 0)
            throw new ArgumentException($"OCI reference '{ociRef}' is malformed.", nameof(ociRef));
        return (registry, repo, reference);
    }

    /// <summary>
    /// Parse a registry <c>WWW-Authenticate: Bearer realm="…",service="…",scope="…"</c>
    /// challenge into its components. Missing fields come back as null.
    /// </summary>
    public static (string? Realm, string? Service, string? Scope) ParseWwwAuthenticate(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return (null, null, null);
        var v = headerValue.Trim();
        if (v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            v = v["Bearer ".Length..];

        string? Field(string name)
        {
            var key = name + "=\"";
            var i = v.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length;
            var end = v.IndexOf('"', i);
            return end < 0 ? null : v[i..end];
        }
        return (Field("realm"), Field("service"), Field("scope"));
    }

    /// <summary>
    /// Pick the artifact layer digest from an image manifest: prefer a
    /// zip-ish mediaType, else the first layer that isn't the config.
    /// Returns null when the JSON isn't an image manifest with layers.
    /// </summary>
    public static string? SelectLayerDigest(string manifestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            if (!doc.RootElement.TryGetProperty("layers", out var layers)
                || layers.ValueKind != JsonValueKind.Array || layers.GetArrayLength() == 0)
                return null;

            string? firstDigest = null;
            foreach (var layer in layers.EnumerateArray())
            {
                var digest = layer.TryGetProperty("digest", out var d) ? d.GetString() : null;
                if (string.IsNullOrEmpty(digest)) continue;
                firstDigest ??= digest;
                var media = layer.TryGetProperty("mediaType", out var m) ? m.GetString() ?? "" : "";
                if (media.Contains("zip", StringComparison.OrdinalIgnoreCase))
                    return digest;
            }
            return firstDigest;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// When the JSON is a multi-arch index/manifest-list, return the
    /// first child manifest's digest; otherwise null (it's already an
    /// image manifest).
    /// </summary>
    public static string? SelectManifestFromIndex(string manifestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            var root = doc.RootElement;
            // An index has "manifests" and no "layers".
            if (root.TryGetProperty("layers", out _)) return null;
            if (!root.TryGetProperty("manifests", out var manifests)
                || manifests.ValueKind != JsonValueKind.Array || manifests.GetArrayLength() == 0)
                return null;
            foreach (var m in manifests.EnumerateArray())
            {
                if (m.TryGetProperty("digest", out var d) && d.GetString() is { Length: > 0 } digest)
                    return digest;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- HTTP + token auth ------------------------------------------

    private static async Task<string> GetStringWithAuthAsync(
        HttpClient http, Uri uri, string accept, string repo, TokenBox auth, CancellationToken ct)
    {
        using (var first = BuildRequest(HttpMethod.Get, uri, accept, auth.Token))
        using (var resp = await http.SendAsync(first, ct).ConfigureAwait(false))
        {
            if (resp.StatusCode != HttpStatusCode.Unauthorized)
            {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }

            // 401 — do the bearer-token dance, then retry once.
            var challenge = resp.Headers.WwwAuthenticate.FirstOrDefault()?.ToString();
            var (realm, service, _) = ParseWwwAuthenticate(challenge);
            if (string.IsNullOrEmpty(realm))
                throw new InvalidOperationException("OCI registry returned 401 without a usable WWW-Authenticate challenge.");
            auth.Token = await FetchTokenAsync(http, realm!, service, repo, ct).ConfigureAwait(false);
        }

        using var retry = BuildRequest(HttpMethod.Get, uri, accept, auth.Token);
        using var retryResp = await http.SendAsync(retry, ct).ConfigureAwait(false);
        retryResp.EnsureSuccessStatusCode();
        return await retryResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string?> FetchTokenAsync(
        HttpClient http, string realm, string? service, string repo, CancellationToken ct)
    {
        var url = $"{realm}?scope=repository:{repo}:pull";
        if (!string.IsNullOrEmpty(service)) url += $"&service={Uri.EscapeDataString(service)}";
        using var resp = await http.GetAsync(new Uri(url), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("token", out var t) && t.GetString() is { Length: > 0 } tok) return tok;
        if (root.TryGetProperty("access_token", out var at) && at.GetString() is { Length: > 0 } atok) return atok;
        return null;
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, Uri uri, string? accept, string? token)
    {
        var req = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrEmpty(accept))
        {
            foreach (var a in accept.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(a));
        }
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }
}
