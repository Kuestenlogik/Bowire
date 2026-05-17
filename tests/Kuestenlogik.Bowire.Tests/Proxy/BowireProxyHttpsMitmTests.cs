// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using Kuestenlogik.Bowire.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Proxy;

/// <summary>
/// End-to-end HTTPS-MITM coverage for the proxy (Tier-3 Stage B).
/// Stands up:
/// <list type="number">
///   <item>An HTTPS upstream on Kestrel with a self-signed cert.</item>
///   <item>A CA + the proxy with MITM enabled.</item>
///   <item>A proxy-aware HttpClient that trusts both the Bowire CA
///   (presented to the client side of the tunnel) and the upstream's
///   self-signed cert (validated by the proxy when it forwards).</item>
/// </list>
/// Then asserts that the request lands at the upstream AND the
/// captured flow carries the decrypted request + response body — the
/// thing that distinguishes Stage B from Stage A's CONNECT-passthrough.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class BowireProxyHttpsMitmTests
{
    private static string FreshTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bowire-mitm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<WebApplication> StartHttpsUpstreamAsync(RequestDelegate handler, CancellationToken ct)
    {
        // Generate a throwaway self-signed cert for the upstream.
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=localhost",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var sanBuilder = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(sanBuilder.Build());
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddDays(7);
        using var raw = req.CreateSelfSigned(notBefore, notAfter);
        var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            raw.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12),
            password: null,
            keyStorageFlags: System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l =>
        {
            l.Protocols = HttpProtocols.Http1;
            l.UseHttps(new HttpsConnectionAdapterOptions { ServerCertificate = cert });
        }));
        var app = builder.Build();
        ((IApplicationBuilder)app).Run(handler);
        await app.StartAsync(ct);
        return app;
    }

    [Fact]
    public async Task ConnectTunnel_MitmDecryptsAndCapturesBody()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var upstream = await StartHttpsUpstreamAsync(async ctx =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            ctx.Response.Headers["X-Bowire-Test"] = "mitm-yes";
            ctx.Response.StatusCode = 201;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(ms.ToArray(), ctx.RequestAborted);
        }, ct);
        var upstreamUri = new Uri(upstream.Urls.First());

        var caDir = FreshTempDir();
        try
        {
            using var ca = BowireProxyCertificateAuthority.LoadOrCreate(caDir);
            var store = new CapturedFlowStore();
            await using var proxy = new BowireProxyServer(store, port: 0, ca);
            await proxy.StartAsync(ct);

            // Proxy-aware client. The proxy presents a leaf cert signed
            // by the Bowire CA — the test trusts the CA root via a
            // custom validation callback (production: install into trust
            // store). The proxy itself trusts the upstream's self-signed
            // cert because it disables peer validation (Stage B v1 — the
            // --strict-tls toggle lands later).
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{proxy.Port}"),
                UseProxy = true,
                ServerCertificateCustomValidationCallback = (_, leaf, chain, _) =>
                {
                    // Accept the leaf when the chain links up to the Bowire CA.
                    if (chain is null) return false;
                    foreach (var el in chain.ChainElements)
                    {
                        if (el.Certificate.Thumbprint == ca.Certificate.Thumbprint) return true;
                    }
                    // Test convenience: also accept when the leaf itself
                    // was issued by the CA's subject (chain-build may
                    // not include the CA when it isn't in the trust store).
                    return string.Equals(leaf?.Issuer, ca.Certificate.Subject, StringComparison.Ordinal);
                },
            };
            using var http = new HttpClient(handler);
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://localhost:{upstreamUri.Port}/echo"))
            {
                Content = new StringContent("{\"hello\":\"mitm-world\"}", Encoding.UTF8, "application/json"),
            };

            using var resp = await http.SendAsync(req, ct);
            var bodyText = await resp.Content.ReadAsStringAsync(ct);

            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            Assert.Contains("mitm-world", bodyText, StringComparison.Ordinal);
            Assert.Equal("mitm-yes", resp.Headers.GetValues("X-Bowire-Test").Single());

            var snap = store.Snapshot();
            Assert.Single(snap);
            var flow = snap[0];
            Assert.Equal("POST", flow.Method);
            Assert.StartsWith("https://", flow.Url, StringComparison.Ordinal);
            Assert.Equal(201, flow.ResponseStatus);
            Assert.Contains("mitm-world", flow.RequestBody ?? "", StringComparison.Ordinal);
            Assert.Contains("mitm-world", flow.ResponseBody ?? "", StringComparison.Ordinal);
            Assert.Null(flow.Error);
        }
        finally { Directory.Delete(caDir, recursive: true); }
    }

    [Fact]
    public async Task ConnectTunnel_WithoutCa_RejectsWith501()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new CapturedFlowStore();
        await using var proxy = new BowireProxyServer(store, port: 0, ca: null);
        await proxy.StartAsync(ct);

        // Raw TCP client speaking CONNECT — we don't need a real upstream
        // because the proxy refuses before any forward.
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, proxy.Port, ct);
        var stream = tcp.GetStream();
        var req = "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(req), ct);
        await stream.FlushAsync(ct);

        // Connection: close means the server closes after sending the
        // status; ReadToEnd via a memory stream captures every byte.
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var response = Encoding.ASCII.GetString(ms.ToArray());
        Assert.Contains("501", response, StringComparison.Ordinal);
        Assert.Contains("disabled", response, StringComparison.OrdinalIgnoreCase);
    }
}
