// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.IntegrationTests.Services;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end mTLS test for the gRPC plugin: brings up a Kestrel HTTP/2 host
/// with <c>ClientCertificateMode.RequireCertificate</c>, a real Greeter
/// service + reflection, and verifies that <see cref="BowireGrpcProtocol"/>
/// honours the <c>__bowireMtls__</c> metadata marker — the cert is attached
/// to the SocketsHttpHandler.SslOptions and the call succeeds.
/// </summary>
public class MtlsGrpcIntegrationTests
{
    [Fact]
    public async Task BowireGrpcProtocol_WithMtlsMarker_CompletesUnaryCall()
    {
        var (clientCertPem, clientKeyPem, _) = GenerateSelfSignedPem("CN=mtls-grpc-client");
        var (_, _, serverCert) = GenerateSelfSignedPem("CN=mtls-grpc-server");
        var serverCertPem = ExportCertOnlyPem(serverCert);

        var port = GetFreeTcpPort();
        var url = $"https://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(opts =>
        {
            // gRPC needs HTTP/2 — pin the listener to HTTP/2 over TLS so
            // ALPN settles cleanly.
            opts.ConfigureEndpointDefaults(lo => lo.Protocols = HttpProtocols.Http2);
            opts.ConfigureHttpsDefaults(https =>
            {
                https.ServerCertificate = serverCert;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (_, _, _) => true;
            });
        });
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddGrpcReflection();

        await using var app = builder.Build();
        app.MapGrpcService<GreeterService>();
        app.MapGrpcReflectionService();
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var protocol = new BowireGrpcProtocol();

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
                [MtlsConfig.MtlsMarkerKey] = mtlsJson
            };

            var result = await protocol.InvokeAsync(
                serverUrl: url,
                service: "test.Greeter",
                method: "SayHello",
                jsonMessages: ["""{"name":"mtls"}"""],
                showInternalServices: false,
                metadata: metadata,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal("OK", result.Status);
            Assert.NotNull(result.Response);
            Assert.Contains("Hello mtls!", result.Response!, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            serverCert.Dispose();
        }
    }

    [Fact]
    public async Task BowireGrpcProtocol_WithoutMtlsMarker_HandshakeFails()
    {
        var (_, _, serverCert) = GenerateSelfSignedPem("CN=mtls-grpc-server");

        var port = GetFreeTcpPort();
        var url = $"https://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.ConfigureEndpointDefaults(lo => lo.Protocols = HttpProtocols.Http2);
            opts.ConfigureHttpsDefaults(https =>
            {
                https.ServerCertificate = serverCert;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (_, _, _) => true;
            });
        });
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddGrpcReflection();

        await using var app = builder.Build();
        app.MapGrpcService<GreeterService>();
        app.MapGrpcReflectionService();
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var protocol = new BowireGrpcProtocol();

            // No __bowireMtls__ metadata → reflection client opens a plain
            // TLS connection without a cert; server rejects the handshake.
            // The reflection failure surfaces as a thrown exception out of
            // GrpcReflectionClient (TLS handshake auth failure), which is
            // the right shape: a hard error, not a friendly InvokeResult.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await protocol.InvokeAsync(
                    serverUrl: url,
                    service: "test.Greeter",
                    method: "SayHello",
                    jsonMessages: ["""{"name":"mtls"}"""],
                    showInternalServices: false,
                    metadata: null,
                    ct: TestContext.Current.CancellationToken);
            });
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            serverCert.Dispose();
        }
    }

    private static (string CertPem, string KeyPem, X509Certificate2 Cert) GenerateSelfSignedPem(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddDnsName("localhost");
        req.CertificateExtensions.Add(sanBuilder.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection {
                new("1.3.6.1.5.5.7.3.1"),
                new("1.3.6.1.5.5.7.3.2")
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

        using var ephemeral = cert;
        var persistable = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
        return (certPem, keyPem, persistable);
    }

    private static string ExportCertOnlyPem(X509Certificate2 cert) =>
        "-----BEGIN CERTIFICATE-----\n"
        + Convert.ToBase64String(cert.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks)
        + "\n-----END CERTIFICATE-----";

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
