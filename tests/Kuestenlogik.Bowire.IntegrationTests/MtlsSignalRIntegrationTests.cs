// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.IntegrationTests.Hubs;
using Kuestenlogik.Bowire.Protocol.SignalR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end mTLS test for the SignalR plugin: brings up a Kestrel HTTPS
/// host with <c>ClientCertificateMode.RequireCertificate</c> + a ChatHub,
/// invokes Echo through <see cref="BowireSignalRProtocol"/> with the
/// magic <c>__bowireMtls__</c> marker in metadata, and asserts the cert
/// reaches the negotiate handshake (HttpMessageHandlerFactory path) plus
/// the WebSocket transport (WebSocketConfiguration path).
/// </summary>
public class MtlsSignalRIntegrationTests
{
    [Fact]
    public async Task BowireSignalRProtocol_WithMtlsMarker_InvokesHubMethod()
    {
        var (clientCertPem, clientKeyPem, _) = GenerateSelfSignedPem("CN=mtls-signalr-client");
        var (_, _, serverCert) = GenerateSelfSignedPem("CN=mtls-signalr-server");
        var serverCertPem = ExportCertOnlyPem(serverCert);

        var port = GetFreeTcpPort();
        var url = $"https://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.ConfigureEndpointDefaults(lo => lo.Protocols = HttpProtocols.Http1AndHttp2);
            opts.ConfigureHttpsDefaults(https =>
            {
                https.ServerCertificate = serverCert;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (_, _, _) => true;
            });
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();

        await using var app = builder.Build();
        app.MapHub<ChatHub>("/chathub");
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var protocol = new BowireSignalRProtocol();

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
                service: "/chathub",
                method: "Echo",
                jsonMessages: ["\"mtls\""],
                showInternalServices: false,
                metadata: metadata,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal("OK", result.Status);
            Assert.NotNull(result.Response);
            Assert.Contains("Echo: mtls", result.Response!, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            serverCert.Dispose();
        }
    }

    [Fact]
    public async Task BowireSignalRProtocol_WithoutMtlsMarker_HandshakeFails()
    {
        var (_, _, serverCert) = GenerateSelfSignedPem("CN=mtls-signalr-server");

        var port = GetFreeTcpPort();
        var url = $"https://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.ConfigureEndpointDefaults(lo => lo.Protocols = HttpProtocols.Http1AndHttp2);
            opts.ConfigureHttpsDefaults(https =>
            {
                https.ServerCertificate = serverCert;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (_, _, _) => true;
            });
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();

        await using var app = builder.Build();
        app.MapHub<ChatHub>("/chathub");
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var protocol = new BowireSignalRProtocol();

            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await protocol.InvokeAsync(
                    serverUrl: url,
                    service: "/chathub",
                    method: "Echo",
                    jsonMessages: ["\"no-cert\""],
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
