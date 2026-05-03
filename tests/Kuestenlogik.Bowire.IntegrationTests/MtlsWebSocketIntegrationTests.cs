// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Protocol.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end mTLS test for the WebSocket plugin: brings up a Kestrel
/// HTTPS host with <c>ClientCertificateMode.RequireCertificate</c> + a
/// trivial echo WebSocket endpoint, opens a <see cref="WebSocketBowireChannel"/>
/// through <see cref="BowireWebSocketProtocol"/> with the magic
/// <c>__bowireMtls__</c> marker in metadata, and round-trips a frame.
/// </summary>
public class MtlsWebSocketIntegrationTests
{
    [Fact]
    public async Task BowireWebSocketProtocol_WithMtlsMarker_RoundTripsFrame()
    {
        var (clientCertPem, clientKeyPem, _) = GenerateSelfSignedPem("CN=mtls-ws-client");
        var (_, _, serverCert) = GenerateSelfSignedPem("CN=mtls-ws-server");
        var serverCertPem = ExportCertOnlyPem(serverCert);

        var port = GetFreeTcpPort();
        var url = $"https://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.ConfigureEndpointDefaults(lo => lo.Protocols = HttpProtocols.Http1);
            opts.ConfigureHttpsDefaults(https =>
            {
                https.ServerCertificate = serverCert;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (_, _, _) => true;
            });
        });
        builder.Logging.ClearProviders();

        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws/echo", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var buf = new byte[1024];
            var result = await ws.ReceiveAsync(buf, ctx.RequestAborted);
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var prefix = System.Text.Encoding.UTF8.GetBytes("echo: ");
                var input = buf.AsMemory(0, result.Count);
                var combined = new byte[prefix.Length + result.Count];
                prefix.CopyTo(combined.AsMemory());
                input.CopyTo(combined.AsMemory(prefix.Length));
                await ws.SendAsync(combined, WebSocketMessageType.Text, true, ctx.RequestAborted);
            }
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ctx.RequestAborted);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var protocol = new BowireWebSocketProtocol();

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

            // The plugin rewrites https:// → wss:// internally; pass the
            // server URL the user actually has on hand.
            var channel = await protocol.OpenChannelAsync(
                serverUrl: url,
                service: "WebSocket",
                method: "/ws/echo",
                showInternalServices: false,
                metadata: metadata,
                ct: TestContext.Current.CancellationToken);

            Assert.NotNull(channel);
            await using var asyncDisposable = (IAsyncDisposable)channel!;

            var sent = await channel.SendAsync("""{"type":"text","text":"mtls ws"}""", TestContext.Current.CancellationToken);
            Assert.True(sent);

            string? echoFrame = null;
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var frame in channel.ReadResponsesAsync(readCts.Token))
            {
                echoFrame = frame;
                break;
            }

            Assert.NotNull(echoFrame);
            Assert.Contains("echo: mtls ws", echoFrame!, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            serverCert.Dispose();
        }
    }

    [Fact]
    public async Task BowireWebSocketProtocol_WithoutMtlsMarker_HandshakeFails()
    {
        var (_, _, serverCert) = GenerateSelfSignedPem("CN=mtls-ws-server");

        var port = GetFreeTcpPort();
        var url = $"https://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.ConfigureEndpointDefaults(lo => lo.Protocols = HttpProtocols.Http1);
            opts.ConfigureHttpsDefaults(https =>
            {
                https.ServerCertificate = serverCert;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (_, _, _) => true;
            });
        });
        builder.Logging.ClearProviders();

        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws/echo", async (HttpContext ctx) =>
        {
            // never reached — handshake should fail before this runs
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ctx.RequestAborted);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var protocol = new BowireWebSocketProtocol();

            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await protocol.OpenChannelAsync(
                    serverUrl: url,
                    service: "WebSocket",
                    method: "/ws/echo",
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
