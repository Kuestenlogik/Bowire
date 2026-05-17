// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography.X509Certificates;
using Kuestenlogik.Bowire.Proxy;

namespace Kuestenlogik.Bowire.Tests.Proxy;

/// <summary>
/// Coverage for the proxy CA + leaf-cert minter (Tier-3 Stage B
/// trust anchor). Verifies CA generation, on-disk persistence, leaf
/// minting (SAN shape + chain-up-to-CA), caching, and the public-cert
/// export the CLI's <c>--export-ca</c> flag drives.
/// </summary>
public sealed class BowireProxyCertificateAuthorityTests
{
    private static string FreshTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bowire-ca-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void LoadOrCreate_FirstRun_GeneratesCaAndWritesPfxAndCrt()
    {
        var dir = FreshTempDir();
        try
        {
            using var ca = BowireProxyCertificateAuthority.LoadOrCreate(dir);
            Assert.True(File.Exists(Path.Combine(dir, "proxy-ca.pfx")));
            Assert.True(File.Exists(Path.Combine(dir, "proxy-ca.crt")));
            Assert.True(ca.Certificate.HasPrivateKey);
            Assert.Contains("Bowire Proxy CA", ca.Certificate.Subject, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadOrCreate_SecondRun_ReusesExistingCa()
    {
        var dir = FreshTempDir();
        try
        {
            string firstThumb;
            using (var first = BowireProxyCertificateAuthority.LoadOrCreate(dir))
                firstThumb = first.Certificate.Thumbprint;

            using var second = BowireProxyCertificateAuthority.LoadOrCreate(dir);
            Assert.Equal(firstThumb, second.Certificate.Thumbprint);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadOrCreate_CorruptPfx_RegeneratesCa()
    {
        var dir = FreshTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "proxy-ca.pfx"), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
            using var ca = BowireProxyCertificateAuthority.LoadOrCreate(dir);
            Assert.True(ca.Certificate.HasPrivateKey);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadOrCreate_MissingPublicCrt_RebuildsIt()
    {
        var dir = FreshTempDir();
        try
        {
            using (var first = BowireProxyCertificateAuthority.LoadOrCreate(dir)) { /* generate */ }
            File.Delete(Path.Combine(dir, "proxy-ca.crt"));
            using var second = BowireProxyCertificateAuthority.LoadOrCreate(dir);
            Assert.True(File.Exists(Path.Combine(dir, "proxy-ca.crt")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void GetOrMintLeaf_PopulatesSanWithHostname()
    {
        var dir = FreshTempDir();
        try
        {
            using var ca = BowireProxyCertificateAuthority.LoadOrCreate(dir);
            using var leaf = ca.GetOrMintLeaf("example.com");

            Assert.True(leaf.HasPrivateKey);
            Assert.Contains("CN=example.com", leaf.Subject, StringComparison.Ordinal);

            // SAN extension carries the requested DNS name.
            var sanExt = leaf.Extensions["2.5.29.17"];
            Assert.NotNull(sanExt);
            var sanText = sanExt!.Format(multiLine: false);
            Assert.Contains("example.com", sanText, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void GetOrMintLeaf_IpHostname_AddsIpAltName()
    {
        var dir = FreshTempDir();
        try
        {
            using var ca = BowireProxyCertificateAuthority.LoadOrCreate(dir);
            using var leaf = ca.GetOrMintLeaf("127.0.0.1");
            var sanExt = leaf.Extensions["2.5.29.17"];
            Assert.NotNull(sanExt);
            Assert.Contains("127.0.0.1", sanExt!.Format(multiLine: false), StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void GetOrMintLeaf_RepeatedCalls_ReturnSameInstance()
    {
        var dir = FreshTempDir();
        try
        {
            using var ca = BowireProxyCertificateAuthority.LoadOrCreate(dir);
            var first = ca.GetOrMintLeaf("api.example.com");
            var second = ca.GetOrMintLeaf("api.example.com");
            Assert.Same(first, second);
            Assert.Equal(1, ca.CachedLeafCount);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void GetOrMintLeaf_DistinctHostsProduceDistinctLeaves()
    {
        var dir = FreshTempDir();
        try
        {
            using var ca = BowireProxyCertificateAuthority.LoadOrCreate(dir);
            using var leafA = ca.GetOrMintLeaf("a.example.com");
            using var leafB = ca.GetOrMintLeaf("b.example.com");
            Assert.NotEqual(leafA.Thumbprint, leafB.Thumbprint);
            Assert.Equal(2, ca.CachedLeafCount);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Leaf_IsChainedToCa()
    {
        var dir = FreshTempDir();
        try
        {
            using var ca = BowireProxyCertificateAuthority.LoadOrCreate(dir);
            using var leaf = ca.GetOrMintLeaf("example.com");

            // The leaf's issuer is the CA's subject — without going to
            // the trust store (the CA isn't installed on the test
            // host), this is the strongest cheap chain assertion.
            Assert.Equal(ca.Certificate.Subject, leaf.Issuer);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Leaf_IsValid_AndExpiresWithin90Days()
    {
        var dir = FreshTempDir();
        try
        {
            using var ca = BowireProxyCertificateAuthority.LoadOrCreate(dir);
            using var leaf = ca.GetOrMintLeaf("example.com");
            // X509Certificate2 returns NotBefore / NotAfter in LOCAL time —
            // normalise to UTC so the comparisons don't drift with the
            // host's timezone offset.
            var notBeforeUtc = leaf.NotBefore.ToUniversalTime();
            var notAfterUtc = leaf.NotAfter.ToUniversalTime();
            Assert.True(notBeforeUtc <= DateTime.UtcNow);
            Assert.True(notAfterUtc <= DateTime.UtcNow.AddDays(91));
            Assert.True(notAfterUtc > DateTime.UtcNow.AddDays(89));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Ca_IsValid_AndExpiresWithin5Years()
    {
        var dir = FreshTempDir();
        try
        {
            using var ca = BowireProxyCertificateAuthority.LoadOrCreate(dir);
            var notBeforeUtc = ca.Certificate.NotBefore.ToUniversalTime();
            var notAfterUtc = ca.Certificate.NotAfter.ToUniversalTime();
            Assert.True(notBeforeUtc <= DateTime.UtcNow);
            Assert.True(notAfterUtc > DateTime.UtcNow.AddYears(4));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ExportPublicCertificate_WritesDerEncodedCrtToTarget()
    {
        var dir = FreshTempDir();
        try
        {
            using var ca = BowireProxyCertificateAuthority.LoadOrCreate(dir);
            var exportPath = Path.Combine(dir, "exported", "bowire-ca.crt");
            ca.ExportPublicCertificate(exportPath);
            Assert.True(File.Exists(exportPath));

            // Loadable as a public cert without a private key.
            using var loaded = X509CertificateLoader.LoadCertificateFromFile(exportPath);
            Assert.Equal(ca.Certificate.Thumbprint, loaded.Thumbprint);
            Assert.False(loaded.HasPrivateKey);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ExportPublicCertificate_NullOrEmptyPath_Throws()
    {
        var dir = FreshTempDir();
        try
        {
            using var ca = BowireProxyCertificateAuthority.LoadOrCreate(dir);
            Assert.ThrowsAny<ArgumentException>(() => ca.ExportPublicCertificate(""));
            Assert.ThrowsAny<ArgumentException>(() => ca.ExportPublicCertificate(null!));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void GetOrMintLeaf_EmptyHostname_Throws()
    {
        var dir = FreshTempDir();
        try
        {
            using var ca = BowireProxyCertificateAuthority.LoadOrCreate(dir);
            Assert.ThrowsAny<ArgumentException>(() => ca.GetOrMintLeaf(""));
            Assert.ThrowsAny<ArgumentException>(() => ca.GetOrMintLeaf("   "));
            Assert.ThrowsAny<ArgumentException>(() => ca.GetOrMintLeaf(null!));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
