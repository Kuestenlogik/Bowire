// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kuestenlogik.Bowire.Proxy;

/// <summary>
/// Self-signed CA that <c>bowire proxy</c> uses to mint leaf
/// certificates for HTTPS interception (Tier-3 Stage B in the
/// security-testing ADR). On first use the CA generates an
/// RSA-2048 self-signed root with a 5-year validity, persists it
/// to <c>~/.bowire/proxy-ca.pfx</c> (private key) and exports a
/// trust-installable <c>~/.bowire/proxy-ca.crt</c> beside it. Every
/// CONNECT target host gets a leaf cert minted on-the-fly: same
/// algorithm, 90-day validity, SAN populated with the requested
/// hostname (and an <c>IP:127.0.0.1</c> alt-name when intercepting
/// loopback traffic). Leaf certs are cached per-host so re-visited
/// targets reuse the same fingerprint.
/// </summary>
/// <remarks>
/// <para>
/// The CA is the trust anchor an operator installs into the local
/// trust store; without that step every intercepted HTTPS request
/// fails the client's chain check. The workbench surfaces the
/// install instructions via the <c>bowire proxy --export-ca</c>
/// CLI flag, which writes the <c>.crt</c> to a caller-chosen path.
/// </para>
/// <para>
/// Thread-safety: leaf minting runs concurrently for parallel
/// CONNECT calls. The cache uses <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// + a lazy factory so each host generates exactly one leaf even
/// under simultaneous handshakes for the same target.
/// </para>
/// </remarks>
public sealed class BowireProxyCertificateAuthority : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<X509Certificate2>> _leafCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _diskLock = new();

    /// <summary>Default directory holding the persisted CA + leaf crt — <c>~/.bowire</c>.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bowire");

    /// <summary>Path of the PKCS#12 file holding the CA private key.</summary>
    public string CaPfxPath { get; }

    /// <summary>Path of the DER-encoded public CA certificate (for trust-store install).</summary>
    public string CaCertPath { get; }

    /// <summary>The CA certificate (public + private key).</summary>
    public X509Certificate2 Certificate { get; }

    private BowireProxyCertificateAuthority(X509Certificate2 ca, string pfxPath, string crtPath)
    {
        Certificate = ca;
        CaPfxPath = pfxPath;
        CaCertPath = crtPath;
    }

    /// <summary>
    /// Load the CA from <paramref name="directory"/> (default
    /// <see cref="DefaultDirectory"/>) — or create + persist a fresh
    /// one if no PFX is present.
    /// </summary>
    public static BowireProxyCertificateAuthority LoadOrCreate(string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        Directory.CreateDirectory(dir);
        var pfx = Path.Combine(dir, "proxy-ca.pfx");
        var crt = Path.Combine(dir, "proxy-ca.crt");

        X509Certificate2? ca = null;
        if (File.Exists(pfx))
        {
            try
            {
                ca = X509CertificateLoader.LoadPkcs12FromFile(pfx, password: null, keyStorageFlags: X509KeyStorageFlags.Exportable);
            }
            catch (CryptographicException)
            {
                // Corrupt PFX — regenerate. Tracking the original would
                // also break any previously-trusted CA; operator can
                // re-import the fresh one.
                ca = null;
            }
        }

        if (ca is null)
        {
            ca = GenerateCa();
            File.WriteAllBytes(pfx, ca.Export(X509ContentType.Pkcs12));
            File.WriteAllBytes(crt, ca.Export(X509ContentType.Cert));
        }
        else if (!File.Exists(crt))
        {
            // PFX present but CRT got deleted — rebuild it.
            File.WriteAllBytes(crt, ca.Export(X509ContentType.Cert));
        }

        return new BowireProxyCertificateAuthority(ca, pfx, crt);
    }

    /// <summary>
    /// Mint a leaf certificate for <paramref name="hostname"/>. Cached
    /// per hostname so two handshakes against the same target reuse
    /// the same fingerprint (TLS sessions / HSTS / HPKP-style pinning
    /// only fire on cert change).
    /// </summary>
    public X509Certificate2 GetOrMintLeaf(string hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        var lazy = _leafCache.GetOrAdd(hostname, h => new Lazy<X509Certificate2>(() => MintLeaf(h)));
        return lazy.Value;
    }

    /// <summary>Number of leaf certs currently cached.</summary>
    public int CachedLeafCount => _leafCache.Count;

    /// <summary>
    /// Copy the public CA certificate to <paramref name="destination"/>
    /// (DER-encoded <c>.crt</c>). Convenience for <c>bowire proxy --export-ca</c>.
    /// </summary>
    public void ExportPublicCertificate(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        lock (_diskLock)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(destination));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(destination, Certificate.Export(X509ContentType.Cert));
        }
    }

    public void Dispose()
    {
        foreach (var lazy in _leafCache.Values)
        {
            if (lazy.IsValueCreated) lazy.Value.Dispose();
        }
        _leafCache.Clear();
        Certificate.Dispose();
    }

    // ---- internal ----

    private X509Certificate2 MintLeaf(string hostname)
    {
        using var rsa = RSA.Create(2048);
        var subject = new System.Security.Cryptography.X509Certificates.X500DistinguishedName($"CN={hostname}, O=Bowire MITM");
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // Leaf-cert extensions: server-auth EKU + SAN.
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") /* serverAuth */ }, critical: false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        if (System.Net.IPAddress.TryParse(hostname, out var ip))
        {
            sanBuilder.AddIpAddress(ip);
        }
        else
        {
            sanBuilder.AddDnsName(hostname);
        }
        req.CertificateExtensions.Add(sanBuilder.Build());

        // Unique serial per leaf — using time-prefixed random so cached
        // leaves still get a recognisably-unique fingerprint.
        var serial = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F; // force positive

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddDays(90);

        using var unsignedLeaf = req.Create(Certificate, notBefore, notAfter, serial);
        var signedLeaf = unsignedLeaf.CopyWithPrivateKey(rsa);
        // Round-trip through PFX export so the private key is bound for
        // SslStream consumption on all platforms (Windows in particular
        // requires this when the source cert is constructed in-memory).
        var pfxBytes = signedLeaf.Export(X509ContentType.Pkcs12);
        signedLeaf.Dispose();
        return X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, keyStorageFlags: X509KeyStorageFlags.Exportable);
    }

    private static X509Certificate2 GenerateCa()
    {
        using var rsa = RSA.Create(2048);
        var subject = new System.Security.Cryptography.X509Certificates.X500DistinguishedName($"CN=Bowire Proxy CA, O=Kuestenlogik, OU=Bowire MITM ({Environment.MachineName})");
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 1, critical: true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, critical: true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddYears(5);

        using var selfSigned = req.CreateSelfSigned(notBefore, notAfter);
        // Round-trip through PFX so SslStream / Kestrel can use the
        // private key on every platform.
        var pfxBytes = selfSigned.Export(X509ContentType.Pkcs12);
        return X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, keyStorageFlags: X509KeyStorageFlags.Exportable);
    }
}
