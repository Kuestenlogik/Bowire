// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// Decoded mTLS client-cert configuration carried inline in the request
/// metadata dict via <see cref="MtlsMarkerKey"/>. Shared between the REST
/// plugin (HttpClientHandler) and the gRPC plugin (SocketsHttpHandler) so
/// every TLS-capable transport speaks the same wire format and only one
/// PEM parser exists in the codebase.
/// </summary>
public sealed record MtlsConfig(
    string CertificatePem,
    string PrivateKeyPem,
    string? Passphrase,
    string? CaCertificatePem,
    bool AllowSelfSigned)
{
    /// <summary>
    /// Magic metadata key the JS mtls auth helper uses to ship its PEM
    /// material to plugin invokers. Plugins strip the marker before
    /// forwarding the rest of the metadata as protocol-level headers.
    /// </summary>
    public const string MtlsMarkerKey = "__bowireMtls__";

    /// <summary>
    /// Look up and parse the marker entry in <paramref name="metadata"/>.
    /// Returns null when the marker is absent or its JSON is malformed.
    /// </summary>
    public static MtlsConfig? TryParseFromMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return null;
        return metadata.TryGetValue(MtlsMarkerKey, out var json)
            ? TryParse(json)
            : null;
    }

    /// <summary>
    /// Returns a copy of the metadata dict with the magic mTLS marker
    /// removed — plugins call this before forwarding metadata as protocol
    /// headers (gRPC <c>Metadata</c>, HTTP request headers, ...).
    /// </summary>
    public static Dictionary<string, string>? StripMarker(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return null;
        var copy = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var (k, v) in metadata)
        {
            if (string.Equals(k, MtlsMarkerKey, StringComparison.Ordinal)) continue;
            copy[k] = v;
        }
        return copy;
    }

    /// <summary>
    /// Parse the JSON shape produced by the JS auth.js layer:
    /// <c>{ certificate, privateKey, passphrase?, caCertificate?, allowSelfSigned? }</c>.
    /// Empty strings on optional fields collapse to null so the encrypted-PEM
    /// path is only taken when a real passphrase was supplied.
    /// </summary>
    public static MtlsConfig? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? Get(string name) =>
                root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString()
                    : null;

            bool GetBool(string name) =>
                root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

            var cert = Get("certificate");
            var key = Get("privateKey");
            if (string.IsNullOrEmpty(cert) || string.IsNullOrEmpty(key)) return null;

            var pass = Get("passphrase");
            var ca = Get("caCertificate");
            return new MtlsConfig(
                cert,
                key,
                string.IsNullOrEmpty(pass) ? null : pass,
                string.IsNullOrEmpty(ca) ? null : ca,
                GetBool("allowSelfSigned"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decode the PEM material into ready-to-use X509 resources. Returns null
    /// if anything fails to parse — caller surfaces a clean error to the user.
    /// Caller owns disposal of the returned certificates (or wraps them in a
    /// dedicated handler-owner — see <see cref="MtlsHandlerOwner"/>).
    /// </summary>
    public bool TryLoadCertificates(out X509Certificate2? clientCert, out X509Certificate2? caCert, out string? error)
    {
        clientCert = null;
        caCert = null;

        X509Certificate2? ephemeral = null;
        try
        {
            ephemeral = string.IsNullOrEmpty(Passphrase)
                ? X509Certificate2.CreateFromPem(CertificatePem, PrivateKeyPem)
                : X509Certificate2.CreateFromEncryptedPem(CertificatePem, PrivateKeyPem, Passphrase);

            // X509Certificate2.CreateFromPem on Windows yields an ephemeral
            // key that some HttpClient versions can't use directly —
            // re-export and re-import as PKCS#12 so the handler picks up a
            // persistable copy. No-op on non-Windows but harmless.
            clientCert = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);

            if (!string.IsNullOrEmpty(CaCertificatePem))
            {
                caCert = X509Certificate2.CreateFromPem(CaCertificatePem);
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            clientCert?.Dispose();
            caCert?.Dispose();
            clientCert = null;
            caCert = null;
            error = "mTLS configuration invalid: " + ex.Message;
            return false;
        }
        finally
        {
            ephemeral?.Dispose();
        }
    }

    /// <summary>
    /// Build the server-cert validation callback that REST and gRPC handlers
    /// can install on their respective TLS layers. <c>AllowSelfSigned=true</c>
    /// returns a no-op accept-anything; otherwise, when <paramref name="caCert"/>
    /// is supplied, a CA-pinning validator that only forgives
    /// <c>UntrustedRoot</c> / <c>PartialChain</c> errors. Returns null when
    /// neither override applies (system trust store handles validation).
    /// </summary>
    public Func<object?, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>? BuildServerValidator(X509Certificate2? caCert)
    {
        if (AllowSelfSigned)
        {
            return (_, _, _, _) => true;
        }
        if (caCert is null) return null;

        return (_, serverCert, chain, errors) =>
        {
            if (serverCert is null || chain is null) return false;
            if (errors == SslPolicyErrors.None) return true;

            // Only an unknown root is curable by pinning the user's CA;
            // hostname mismatches and revocation failures stay rejected.
            var unknownRootOnly = errors == SslPolicyErrors.RemoteCertificateChainErrors
                && chain.ChainStatus.All(s =>
                    s.Status == X509ChainStatusFlags.NoError
                    || s.Status == X509ChainStatusFlags.UntrustedRoot
                    || s.Status == X509ChainStatusFlags.PartialChain);
            if (!unknownRootOnly) return false;

            using var pinned = new X509Chain();
            pinned.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            pinned.ChainPolicy.CustomTrustStore.Add(caCert);
            pinned.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return pinned.Build(serverCert);
        };
    }
}
