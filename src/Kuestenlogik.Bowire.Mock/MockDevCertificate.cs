// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kuestenlogik.Bowire.Mock;

/// <summary>
/// TLS certificate sourcing for the standalone mock's HTTPS listener (#410):
/// an in-memory self-signed <c>localhost</c> certificate by default, or an
/// operator-supplied PFX / PEM.
/// </summary>
internal static class MockDevCertificate
{
    /// <summary>
    /// Generate a fresh self-signed certificate for <c>localhost</c> (with
    /// loopback SANs), valid for one year. Untrusted by design — the mock is a
    /// dev/test tool; clients probing an HTTPS mock accept the cert explicitly
    /// (or import it). Exported + re-imported through a PKCS#12 blob so the
    /// private key is usable by Kestrel's TLS on every OS.
    /// </summary>
    public static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
        // Round-trip through PKCS#12 so the key lands in a keystore Kestrel can
        // use (an ephemeral in-memory key trips SChannel on Windows).
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), (string?)null);
    }

    /// <summary>
    /// Load an operator-supplied certificate. A <c>.pfx</c>/<c>.p12</c> file is
    /// read as PKCS#12 (with the optional password); anything else is treated
    /// as a PEM bundle (cert + key in one file, or a cert whose key sits next
    /// to it — <c>CreateFromPemFile</c> resolves both).
    /// </summary>
    public static X509Certificate2 Load(string path, string? password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var ext = Path.GetExtension(path);
        if (string.Equals(ext, ".pfx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".p12", StringComparison.OrdinalIgnoreCase))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }

        // PEM: CreateFromPemFile pairs the cert with its private key. Re-export
        // through PKCS#12 for the same Kestrel-key-usability reason as above.
        using var pem = X509Certificate2.CreateFromPemFile(path);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), (string?)null);
    }
}
