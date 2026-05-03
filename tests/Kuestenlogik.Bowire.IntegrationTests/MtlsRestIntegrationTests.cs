// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Rest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end test for the mTLS auth helper: spins up a Kestrel host with
/// <c>ClientCertificateMode.RequireCertificate</c>, fires a request through
/// <see cref="RestInvoker"/> with a magic-prefixed metadata entry carrying
/// the client cert, and asserts the server actually saw the cert on the
/// TLS handshake.
///
/// Self-signed certs are generated in-memory so the test has no fixture
/// files. The server cert is registered as the trust anchor on the client
/// side via the helper's CA-pinning path; the client cert isn't validated
/// for chain by Kestrel (mode is just RequireCertificate, no validator), so
/// the handshake succeeds as long as the cert is presented.
/// </summary>
public class MtlsRestIntegrationTests
{
    [Fact]
    public async Task RestInvoker_WithMtlsMarker_PresentsClientCertOnHandshake()
    {
        var (clientCertPem, clientKeyPem, _) = GenerateSelfSignedPem("CN=mtls-test-client");
        var (_, _, serverCert) = GenerateSelfSignedPem("CN=mtls-test-server");
        var serverCertPem = ExportCertOnlyPem(serverCert);

        var port = GetFreeTcpPort();
        var url = $"https://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.ConfigureHttpsDefaults(https =>
            {
                https.ServerCertificate = serverCert;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                // Skip Kestrel-side validation — we only care that a cert was
                // presented on the handshake. Reject the chain check explicitly
                // so a system store oddity doesn't fail the test on CI.
                https.ClientCertificateValidation = (_, _, _) => true;
            });
        });
        builder.Logging.ClearProviders();

        await using var app = builder.Build();
        app.MapGet("/echo-cert", (HttpContext ctx) =>
        {
            var presented = ctx.Connection.ClientCertificate;
            return Results.Json(new
            {
                seen = presented is not null,
                subject = presented?.Subject ?? string.Empty
            });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var methodInfo = new BowireMethodInfo(
                Name: "EchoCert",
                FullName: "EchoCert",
                ClientStreaming: false,
                ServerStreaming: false,
                InputType: new BowireMessageInfo("Empty", "Empty", []),
                OutputType: new BowireMessageInfo("Response", "Response", []),
                MethodType: "Unary")
            {
                HttpMethod = "GET",
                HttpPath = "/echo-cert"
            };

            var mtlsJson = $$"""
                {
                    "certificate": {{System.Text.Json.JsonSerializer.Serialize(clientCertPem)}},
                    "privateKey": {{System.Text.Json.JsonSerializer.Serialize(clientKeyPem)}},
                    "caCertificate": {{System.Text.Json.JsonSerializer.Serialize(serverCertPem)}},
                    "allowSelfSigned": false
                }
                """;

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["__bowireMtls__"] = mtlsJson
            };

            // The shared HttpClient is unused on the mTLS path — RestInvoker
            // builds a per-call client when the marker is present — but the
            // signature still requires one.
            using var unusedClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            var result = await RestInvoker.InvokeAsync(
                unusedClient, url, methodInfo,
                jsonMessages: ["{}"],
                requestMetadata: metadata,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal("OK", result.Status);
            Assert.NotNull(result.Response);
            Assert.Contains("\"seen\":true", result.Response!, StringComparison.Ordinal);
            Assert.Contains("CN=mtls-test-client", result.Response!, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            serverCert.Dispose();
        }
    }

    [Fact]
    public async Task RestInvoker_WithoutMtlsMarker_ServerRejectsHandshake()
    {
        var (_, _, serverCert) = GenerateSelfSignedPem("CN=mtls-test-server");

        var port = GetFreeTcpPort();
        var url = $"https://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.ConfigureHttpsDefaults(https =>
            {
                https.ServerCertificate = serverCert;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (_, _, _) => true;
            });
        });
        builder.Logging.ClearProviders();

        await using var app = builder.Build();
        app.MapGet("/echo-cert", () => Results.Ok("should never reach"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var methodInfo = new BowireMethodInfo(
                Name: "EchoCert",
                FullName: "EchoCert",
                ClientStreaming: false,
                ServerStreaming: false,
                InputType: new BowireMessageInfo("Empty", "Empty", []),
                OutputType: new BowireMessageInfo("Response", "Response", []),
                MethodType: "Unary")
            {
                HttpMethod = "GET",
                HttpPath = "/echo-cert"
            };

            // Build a client that trusts the server but doesn't ship a client
            // cert. Server should refuse the connection.
#pragma warning disable CA5400, CA2000
            var noCertHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                CheckCertificateRevocationList = false
            };
            using var noMtlsClient = new HttpClient(noCertHandler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
#pragma warning restore CA5400, CA2000

            var result = await RestInvoker.InvokeAsync(
                noMtlsClient, url, methodInfo,
                jsonMessages: ["{}"],
                requestMetadata: null,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal("NetworkError", result.Status);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            serverCert.Dispose();
        }
    }

    /// <summary>
    /// Produces a fresh self-signed cert + PKCS#8 key as PEM strings, plus
    /// the in-memory <see cref="X509Certificate2"/> for server-side use.
    /// </summary>
    private static (string CertPem, string KeyPem, X509Certificate2 Cert) GenerateSelfSignedPem(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // Server cert needs the right EKU + a SAN for "127.0.0.1" so .NET's
        // hostname matcher accepts it. Both halves are added even for the
        // client cert; harmless on a client-only flow.
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddDnsName("localhost");
        req.CertificateExtensions.Add(sanBuilder.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection {
                new("1.3.6.1.5.5.7.3.1"), // server auth
                new("1.3.6.1.5.5.7.3.2")  // client auth
            }, critical: false));

        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        var certPem = "-----BEGIN CERTIFICATE-----\n"
            + Convert.ToBase64String(cert.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks)
            + "\n-----END CERTIFICATE-----";
        var keyPem = "-----BEGIN PRIVATE KEY-----\n"
            + Convert.ToBase64String(rsa.ExportPkcs8PrivateKey(), Base64FormattingOptions.InsertLineBreaks)
            + "\n-----END PRIVATE KEY-----";

        // Re-load via PKCS#12 so the cert carries a persistable private key
        // — required for Kestrel to use it as a server certificate.
        using var ephemeral = cert;
        var persistable = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
        return (certPem, keyPem, persistable);
    }

    private static string ExportCertOnlyPem(X509Certificate2 cert)
    {
        return "-----BEGIN CERTIFICATE-----\n"
            + Convert.ToBase64String(cert.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks)
            + "\n-----END CERTIFICATE-----";
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
