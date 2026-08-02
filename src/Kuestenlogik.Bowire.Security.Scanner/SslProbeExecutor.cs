// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Runs a translated Nuclei <c>ssl:</c> template (#491, #35 Phase 2g):
/// complete a TLS handshake and match over the certificate the peer presented.
/// </summary>
/// <remarks>
/// <para>
/// <b>No trust is ever granted.</b> The probe has to see certificates a normal
/// client rejects — expired, self-signed, wrong-host — because those are the
/// findings. It does that without accepting any of them: the validation
/// callback copies the certificate and returns <c>false</c>, which aborts the
/// handshake immediately after the certificate message. That is everything the
/// matchers need and nothing more, and it means the scanner never completes a
/// TLS session with a peer it could not verify. The alternative — a callback
/// that returns <c>true</c> unconditionally — would be both a weaker posture
/// and a CA5359 suppression.
/// </para>
/// <para>
/// The cost is the negotiated protocol and cipher, which are only known once a
/// handshake finishes. Those are not lost: the scanner's built-in checks
/// already enumerate TLS versions on the target.
/// </para>
/// <para>
/// <b>What the matcher sees.</b> The certificate renders into one body, one
/// <c>field: value</c> per line, so <c>word</c> and <c>regex</c> matchers work
/// against it. Nuclei's own <c>dsl</c> matchers (<c>not_after &lt; now</c>) still
/// do not translate — that is a translator-wide gap, not an ssl one — so the
/// derived <c>expired</c> / <c>self_signed</c> lines below give word matchers
/// something to hit for the two cases templates ask for most.
/// </para>
/// </remarks>
public static class SslProbeExecutor
{
    /// <summary>
    /// Handshake against the step's address and render the peer certificate.
    /// </summary>
    /// <param name="probe">The recording step to run; Service is host:port.</param>
    /// <param name="timeoutSeconds">Connect + handshake budget.</param>
    /// <param name="now">Clock for the derived expiry lines; injected so the
    /// test does not have to mint a certificate that expires while it runs.</param>
    /// <param name="ct">Cancels the probe.</param>
    public static async Task<AttackProbeResponse> ExecuteAsync(
        BowireRecordingStep probe,
        int timeoutSeconds = 10,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var (host, port) = ParseAddress(probe.Service ?? string.Empty);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        var sw = Stopwatch.StartNew();
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, budget.Token).ConfigureAwait(false);

        X509Certificate2? peer = null;
        await using var stream = client.GetStream();
        await using var tls = new SslStream(stream, leaveInnerStreamOpen: false, (_, cert, _, _) =>
        {
            if (cert is not null) peer = new X509Certificate2(cert);
            // Refuse, always. See the remarks: the certificate is the product,
            // and finishing the handshake would mean trusting a peer whose
            // certificate is very often exactly what we are reporting on.
            return false;
        });

        try
        {
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.None,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                budget.Token).ConfigureAwait(false);
        }
        catch (AuthenticationException) when (peer is not null)
        {
            // Expected: we rejected the certificate on purpose, after copying it.
        }
        sw.Stop();

        if (peer is null)
        {
            throw new InvalidOperationException(
                $"TLS handshake with {host}:{port} presented no certificate.");
        }

        using (peer)
        {
            return new AttackProbeResponse
            {
                Status = 0,
                Body = Render(peer, now ?? DateTimeOffset.UtcNow),
                LatencyMs = (int)sw.ElapsedMilliseconds,
            };
        }
    }

    /// <summary>
    /// Split <c>host:port</c> for an <c>ssl:</c> address. Unlike the network
    /// transport a missing port defaults to 443 — an ssl: template without one
    /// unambiguously means "the TLS port", where a raw socket template does not.
    /// </summary>
    public static (string Host, int Port) ParseAddress(string address)
    {
        var value = address.Trim();
        if (value.Length == 0)
        {
            throw new InvalidOperationException("ssl template carries no address to connect to.");
        }
        if (value.Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ssl template address still holds an unresolved placeholder ({value}) — bind a target so it can be substituted.");
        }

        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1) return (value, 443);

        var portText = value[(colon + 1)..];
        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            return (value, 443);
        }
        return (value[..colon], port);
    }

    /// <summary>
    /// Flatten a certificate into the body matchers read. Field names follow
    /// Nuclei's own vocabulary so a template's words land on something
    /// recognisable.
    /// </summary>
    public static string Render(X509Certificate2 certificate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var notBefore = certificate.NotBefore.ToUniversalTime();
        var notAfter = certificate.NotAfter.ToUniversalTime();
        var selfSigned = string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal);

        var sb = new StringBuilder();
        sb.Append("subject: ").Append(certificate.Subject).Append('\n');
        sb.Append("subject_cn: ").Append(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false)).Append('\n');
        sb.Append("issuer: ").Append(certificate.Issuer).Append('\n');
        sb.Append("issuer_cn: ").Append(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true)).Append('\n');
        sb.Append("serial: ").Append(certificate.SerialNumber).Append('\n');
        sb.Append("fingerprint_sha256: ").Append(Convert.ToHexString(certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256))).Append('\n');
        sb.Append("not_before: ").Append(notBefore.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("not_after: ").Append(notAfter.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("signature_algorithm: ").Append(certificate.SignatureAlgorithm.FriendlyName ?? certificate.SignatureAlgorithm.Value).Append('\n');

        // Derived lines. `dsl` matchers do not translate, so these give the two
        // conditions ssl: templates actually assert something a word matcher
        // can reach.
        sb.Append("expired: ").Append(now > notAfter ? "true" : "false").Append('\n');
        sb.Append("not_yet_valid: ").Append(now < notBefore ? "true" : "false").Append('\n');
        sb.Append("self_signed: ").Append(selfSigned ? "true" : "false").Append('\n');

        foreach (var name in SubjectAlternativeNames(certificate))
        {
            sb.Append("dns_name: ").Append(name).Append('\n');
        }

        return sb.ToString();
    }

    private static IEnumerable<string> SubjectAlternativeNames(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != "2.5.29.17") continue;
            // FormatMultiline yields one entry per line as "DNS Name=host".
            foreach (var line in extension.Format(multiLine: true)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                yield return eq >= 0 ? line[(eq + 1)..].Trim() : line;
            }
        }
    }
}
