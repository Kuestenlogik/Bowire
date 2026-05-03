// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the JSON wire shape of the mTLS auth helper marker, plus the
/// PEM-to-handler conversion. We generate a throwaway self-signed cert in
/// memory so the tests don't need any on-disk fixtures.
/// </summary>
public class MtlsConfigTests
{
    [Fact]
    public void TryParse_ValidJson_ParsesAllFields()
    {
        var json = """
            {
                "certificate": "CERT_PEM",
                "privateKey": "KEY_PEM",
                "passphrase": "secret",
                "caCertificate": "CA_PEM",
                "allowSelfSigned": true
            }
            """;

        var cfg = MtlsConfig.TryParse(json);

        Assert.NotNull(cfg);
        Assert.Equal("CERT_PEM", cfg!.CertificatePem);
        Assert.Equal("KEY_PEM", cfg.PrivateKeyPem);
        Assert.Equal("secret", cfg.Passphrase);
        Assert.Equal("CA_PEM", cfg.CaCertificatePem);
        Assert.True(cfg.AllowSelfSigned);
    }

    [Fact]
    public void TryParse_OmitsOptionalFields_StillParses()
    {
        var json = """{"certificate":"CERT_PEM","privateKey":"KEY_PEM"}""";

        var cfg = MtlsConfig.TryParse(json);

        Assert.NotNull(cfg);
        Assert.Equal("CERT_PEM", cfg!.CertificatePem);
        Assert.Equal("KEY_PEM", cfg.PrivateKeyPem);
        Assert.Null(cfg.Passphrase);
        Assert.Null(cfg.CaCertificatePem);
        Assert.False(cfg.AllowSelfSigned);
    }

    [Fact]
    public void TryParse_EmptyPassphrase_NormalisesToNull()
    {
        // The JS layer ships passphrase as "" when the user leaves the field
        // blank; the parser collapses empty strings to null so the encrypted-
        // PEM code path is only taken when a real passphrase was supplied.
        var json = """{"certificate":"CERT","privateKey":"KEY","passphrase":""}""";

        var cfg = MtlsConfig.TryParse(json);

        Assert.NotNull(cfg);
        Assert.Null(cfg!.Passphrase);
    }

    [Fact]
    public void TryParse_MissingCertificate_ReturnsNull()
    {
        var json = """{"privateKey":"KEY_PEM"}""";
        Assert.Null(MtlsConfig.TryParse(json));
    }

    [Fact]
    public void TryParse_MissingPrivateKey_ReturnsNull()
    {
        var json = """{"certificate":"CERT_PEM"}""";
        Assert.Null(MtlsConfig.TryParse(json));
    }

    [Fact]
    public void TryParse_InvalidJson_ReturnsNull()
    {
        Assert.Null(MtlsConfig.TryParse("not json"));
        Assert.Null(MtlsConfig.TryParse("[]"));
        Assert.Null(MtlsConfig.TryParse(""));
    }

    [Fact]
    public void CreateHttpClientHandler_ValidPem_BuildsHandlerWithClientCert()
    {
        var (certPem, keyPem) = GenerateSelfSignedPem();
        var cfg = new MtlsConfig(certPem, keyPem, null, null, AllowSelfSigned: false);

        using var owner = MtlsHandlerOwner.CreateHttpClientHandler(cfg, out var error);

        Assert.NotNull(owner);
        Assert.Null(error);
        var handler = Assert.IsType<HttpClientHandler>(owner!.Handler);
        Assert.Single(handler.ClientCertificates);
        Assert.Equal(ClientCertificateOption.Manual, handler.ClientCertificateOptions);
    }

    [Fact]
    public void CreateHttpClientHandler_AllowSelfSigned_AcceptsAnyServerCert()
    {
        var (certPem, keyPem) = GenerateSelfSignedPem();
        var cfg = new MtlsConfig(certPem, keyPem, null, null, AllowSelfSigned: true);

        using var owner = MtlsHandlerOwner.CreateHttpClientHandler(cfg, out _);

        Assert.NotNull(owner);
        var handler = (HttpClientHandler)owner!.Handler;
        var validator = handler.ServerCertificateCustomValidationCallback;
        Assert.NotNull(validator);
        Assert.True(validator!(null!, null, null, System.Net.Security.SslPolicyErrors.None));
        Assert.True(validator!(null!, null, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void CreateSocketsHttpHandler_ValidPem_AttachesCertViaSslOptions()
    {
        var (certPem, keyPem) = GenerateSelfSignedPem();
        var cfg = new MtlsConfig(certPem, keyPem, null, null, AllowSelfSigned: false);

        using var owner = MtlsHandlerOwner.CreateSocketsHttpHandler(cfg, out var error);

        Assert.NotNull(owner);
        Assert.Null(error);
        var handler = Assert.IsType<SocketsHttpHandler>(owner!.Handler);
        Assert.NotNull(handler.SslOptions.ClientCertificates);
        Assert.Single(handler.SslOptions.ClientCertificates!);
    }

    [Fact]
    public void CreateHttpClientHandler_GarbagePem_ReturnsNullWithError()
    {
        var cfg = new MtlsConfig(
            "-----BEGIN CERTIFICATE-----\nnot-real-base64\n-----END CERTIFICATE-----",
            "-----BEGIN PRIVATE KEY-----\nalso-not-real\n-----END PRIVATE KEY-----",
            null, null, AllowSelfSigned: false);

        using var owner = MtlsHandlerOwner.CreateHttpClientHandler(cfg, out var error);

        Assert.Null(owner);
        Assert.NotNull(error);
        Assert.Contains("mTLS", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void StripMarker_RemovesMtlsEntry_KeepsOthers()
    {
        var input = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer token",
            [MtlsConfig.MtlsMarkerKey] = "{}",
            ["X-Custom"] = "value"
        };

        var stripped = MtlsConfig.StripMarker(input);

        Assert.NotNull(stripped);
        Assert.Equal(2, stripped!.Count);
        Assert.False(stripped.ContainsKey(MtlsConfig.MtlsMarkerKey));
        Assert.Equal("Bearer token", stripped["Authorization"]);
        Assert.Equal("value", stripped["X-Custom"]);
    }

    [Fact]
    public void TryParseFromMetadata_FindsMarker()
    {
        var json = """{"certificate":"CERT","privateKey":"KEY"}""";
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MtlsConfig.MtlsMarkerKey] = json
        };

        var cfg = MtlsConfig.TryParseFromMetadata(meta);

        Assert.NotNull(cfg);
        Assert.Equal("CERT", cfg!.CertificatePem);
    }

    /// <summary>
    /// Generates a fresh self-signed RSA cert + PKCS#8 private key as PEM
    /// strings. Lives in test code so the tests don't depend on any baked
    /// fixture files or a local PKI.
    /// </summary>
    private static (string CertPem, string KeyPem) GenerateSelfSignedPem()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=bowire-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        var certPem = "-----BEGIN CERTIFICATE-----\n"
            + Convert.ToBase64String(cert.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks)
            + "\n-----END CERTIFICATE-----";
        var keyPem = "-----BEGIN PRIVATE KEY-----\n"
            + Convert.ToBase64String(rsa.ExportPkcs8PrivateKey(), Base64FormattingOptions.InsertLineBreaks)
            + "\n-----END PRIVATE KEY-----";
        return (certPem, keyPem);
    }
}
