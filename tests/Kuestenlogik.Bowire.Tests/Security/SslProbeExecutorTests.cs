// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kuestenlogik.Bowire.Security;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// #491 (#35 Phase 2g) — the TLS transport pass.
///
/// The rendering is tested directly against minted certificates rather than
/// through a socket: what matters is which facts reach a matcher, and a
/// handshake in the middle only adds a second thing that can fail.
/// </summary>
public sealed class SslProbeExecutorTests
{
    private static X509Certificate2 Mint(
        string subject = "CN=shop.example.com",
        string? issuer = null,
        int daysValidFrom = -30,
        int daysValidTo = 30)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("shop.example.com");
        san.AddDnsName("www.shop.example.com");
        request.CertificateExtensions.Add(san.Build());

        var from = DateTimeOffset.UtcNow.AddDays(daysValidFrom);
        var to = DateTimeOffset.UtcNow.AddDays(daysValidTo);

        if (issuer is null) return request.CreateSelfSigned(from, to);

        // A distinct issuer name is enough to exercise the self_signed line;
        // signing with a real CA key is not what is under test here.
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(issuer, caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(from.AddDays(-1), to.AddDays(1));
        return request.Create(ca, from, to, [1, 2, 3, 4]);
    }

    [Fact]
    public void Render_Exposes_The_Fields_Templates_Match_On()
    {
        using var cert = Mint();

        var body = SslProbeExecutor.Render(cert, DateTimeOffset.UtcNow);

        Assert.Contains("subject_cn: shop.example.com", body, StringComparison.Ordinal);
        Assert.Contains("issuer:", body, StringComparison.Ordinal);
        Assert.Contains("not_after:", body, StringComparison.Ordinal);
        Assert.Contains("fingerprint_sha256:", body, StringComparison.Ordinal);
        Assert.Contains("dns_name: www.shop.example.com", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Marks_An_Expired_Certificate()
    {
        // `dsl` matchers do not translate, so `expired: true` is what a word
        // matcher can actually reach.
        using var cert = Mint(daysValidFrom: -60, daysValidTo: -1);

        var body = SslProbeExecutor.Render(cert, DateTimeOffset.UtcNow);

        Assert.Contains("expired: true", body, StringComparison.Ordinal);
        Assert.True(AttackPredicateEvaluator.Evaluate(
            new AttackPredicate { BodyContains = "expired: true" },
            new AttackProbeResponse { Status = 0, Body = body }));
    }

    [Fact]
    public void Render_Does_Not_Mark_A_Valid_Certificate_Expired()
    {
        using var cert = Mint();

        var body = SslProbeExecutor.Render(cert, DateTimeOffset.UtcNow);

        Assert.Contains("expired: false", body, StringComparison.Ordinal);
        Assert.Contains("not_yet_valid: false", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Uses_The_Injected_Clock()
    {
        // Pinning the clock is why the expiry lines are testable at all —
        // otherwise the assertion drifts with the wall clock.
        using var cert = Mint(daysValidFrom: -10, daysValidTo: 10);

        var future = SslProbeExecutor.Render(cert, DateTimeOffset.UtcNow.AddDays(365));
        Assert.Contains("expired: true", future, StringComparison.Ordinal);

        var past = SslProbeExecutor.Render(cert, DateTimeOffset.UtcNow.AddDays(-365));
        Assert.Contains("not_yet_valid: true", past, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Distinguishes_Self_Signed_From_Ca_Issued()
    {
        using var selfSigned = Mint();
        Assert.Contains("self_signed: true",
            SslProbeExecutor.Render(selfSigned, DateTimeOffset.UtcNow), StringComparison.Ordinal);

        using var caIssued = Mint(issuer: "CN=Example Root CA");
        Assert.Contains("self_signed: false",
            SslProbeExecutor.Render(caIssued, DateTimeOffset.UtcNow), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseAddress_Defaults_To_443_Without_A_Port()
    {
        // Unlike a raw socket template, an ssl: address without a port
        // unambiguously means the TLS port.
        Assert.Equal(("example.com", 443), SslProbeExecutor.ParseAddress("example.com"));
        Assert.Equal(("example.com", 8443), SslProbeExecutor.ParseAddress("example.com:8443"));
    }

    [Fact]
    public void ParseAddress_Refuses_An_Unresolved_Placeholder()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SslProbeExecutor.ParseAddress("{{Host}}:{{Port}}"));
        Assert.Contains("placeholder", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
