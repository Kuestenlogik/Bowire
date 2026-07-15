// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Oast;

namespace Kuestenlogik.Bowire.Oast.Tests;

/// <summary>
/// Coverage for the OAST interactsh client (#35 Phase 2f). The crypto and the
/// host layout are pinned against external ground truth, not against
/// themselves: a wrong counter increment or a stock base32 alphabet would
/// round-trip perfectly here while making the real server correlate nothing.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
public sealed class InteractshClientTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", "", StringComparison.Ordinal));

    // ---------------- AES-CTR vs NIST SP 800-38A ----------------

    [Fact]
    public void AesCtr_matches_the_NIST_SP800_38A_F5_vector()
    {
        // NIST SP 800-38A, F.5.1 CTR-AES128.Encrypt. This is the whole reason
        // the transform is hand-built: .NET exposes no CTR, interactsh
        // encrypts with it, and a self-round-trip test would happily pass with
        // a broken counter. Encrypting the published plaintext must yield the
        // published ciphertext, byte for byte.
        var key = Hex("2b7e151628aed2a6abf7158809cf4f3c");
        var counter = Hex("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        var plaintext = Hex(
            "6bc1bee22e409f96e93d7e117393172a" +
            "ae2d8a571e03ac9c9eb76fac45af8e51" +
            "30c81c46a35ce411e5fbc1191a0a52ef" +
            "f69f2445df4f9b17ad2b417be66c3710");
        var expected = Hex(
            "874d6191b620e3261bef6864990db6ce" +
            "9806f66b7970fdff8617187bb9fffdff" +
            "5ae4df3edbd5d35e5b4f09020db03eab" +
            "1e031dda2fbe03d1792170a0f3009cee");

        var actual = InteractshClient.AesCtrTransform(key, counter, plaintext);

        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));
    }

    [Fact]
    public void AesCtr_is_symmetric_so_decrypt_recovers_the_plaintext()
    {
        // Same vector, other direction — CTR decryption is the identical
        // keystream XOR, which is what the poll path relies on.
        var key = Hex("2b7e151628aed2a6abf7158809cf4f3c");
        var counter = Hex("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        var ciphertext = Hex("874d6191b620e3261bef6864990db6ce");
        var expected = Hex("6bc1bee22e409f96e93d7e117393172a");

        var actual = InteractshClient.AesCtrTransform(key, counter, ciphertext);

        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));
    }

    [Fact]
    public void AesCtr_handles_a_partial_trailing_block()
    {
        // Interaction JSON is never a neat multiple of 16 bytes; the last
        // block must be XOR'd only as far as the data goes, without padding.
        var key = Hex("2b7e151628aed2a6abf7158809cf4f3c");
        var counter = Hex("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        var plaintext = Encoding.UTF8.GetBytes("a partial block, not 16-aligned!!!");

        var round = InteractshClient.AesCtrTransform(
            key, counter, InteractshClient.AesCtrTransform(key, counter, plaintext));

        Assert.Equal(plaintext.Length, round.Length);
        Assert.Equal("a partial block, not 16-aligned!!!", Encoding.UTF8.GetString(round));
    }

    // ---------------- callback host layout ----------------

    [Fact]
    public void Allocate_builds_a_33_char_label_under_the_server_domain()
    {
        var client = new InteractshClient("https://oast.example.com", httpHandler: new StubHandler(_ => Json("{}")));
        var a = client.Allocate();

        Assert.EndsWith(".oast.example.com", a.CallbackHost, StringComparison.Ordinal);
        var label = a.CallbackHost[..a.CallbackHost.IndexOf('.', StringComparison.Ordinal)];
        // 20 correlation + 13 nonce — the slice the server keys on.
        Assert.Equal(33, label.Length);
        Assert.StartsWith(a.CorrelationId, label, StringComparison.Ordinal);
        Assert.Equal(20, a.CorrelationId.Length);
        // Must survive a DNS label.
        Assert.Matches("^[a-z0-9]+$", label);
    }

    [Fact]
    public void Allocate_shares_the_correlation_id_but_never_the_host()
    {
        var client = new InteractshClient("https://oast.example.com", httpHandler: new StubHandler(_ => Json("{}")));
        var first = client.Allocate();
        var second = client.Allocate();

        // One session polls one correlation id...
        Assert.Equal(first.CorrelationId, second.CorrelationId);
        // ...but two probes must never be credited with each other's callback.
        Assert.NotEqual(first.CallbackHost, second.CallbackHost);
    }

    [Fact]
    public void Constructor_rejects_a_non_http_server()
    {
        Assert.Throws<ArgumentException>(() => new InteractshClient("oast.example.com"));
        Assert.Throws<ArgumentException>(() => new InteractshClient("dns://oast.example.com"));
    }

    // ---------------- register + poll ----------------

    [Fact]
    public async Task RegisterAsync_posts_the_pem_public_key_and_correlation_id()
    {
        string? body = null;
        string? path = null;
        var client = new InteractshClient("https://oast.example.com", httpHandler: new StubHandler(req =>
        {
            path = req.RequestUri!.AbsolutePath;
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("{}");
        }));

        await client.RegisterAsync(TestContext.Current.CancellationToken);

        Assert.Equal("/register", path);
        using var doc = JsonDocument.Parse(body!);
        // Field names are the server's, verbatim — a rename silently 400s.
        var pubKey = doc.RootElement.GetProperty("PublicKey").GetString()!;
        Assert.Equal(20, doc.RootElement.GetProperty("CorrelationID").GetString()!.Length);
        Assert.NotEmpty(doc.RootElement.GetProperty("SecretKey").GetString()!);
        // The server expects the PEM text base64'd, not raw DER.
        var pem = Encoding.UTF8.GetString(Convert.FromBase64String(pubKey));
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", pem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterAsync_surfaces_a_server_rejection()
    {
        var client = new InteractshClient("https://oast.example.com",
            httpHandler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var ex = await Assert.ThrowsAsync<OastException>(
            () => client.RegisterAsync(TestContext.Current.CancellationToken));
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollAsync_decrypts_an_rsa_wrapped_aes_ctr_interaction()
    {
        // The full server-side shape: wrap a random AES key to the client's
        // public key with RSA-OAEP/SHA-256, then hand back an AES-CTR item
        // with the IV prefixed. This is the path that proves the client can
        // read what a real server sends.
        var aesKey = RandomNumberGenerator.GetBytes(32);
        var interaction = """
            {"protocol":"dns","unique-id":"abc","full-id":"abcnonce.oast.example.com",
             "q-type":"A","remote-address":"203.0.113.7","timestamp":"2026-07-15T10:00:00Z",
             "raw-request":";; opcode: QUERY"}
            """;

        string? capturedPubKey = null;
        var client = new InteractshClient("https://oast.example.com", httpHandler: new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/register")
            {
                using var d = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                capturedPubKey = d.RootElement.GetProperty("PublicKey").GetString();
                return Json("{}");
            }

            // /poll — encrypt exactly as the server does.
            using var rsa = RSA.Create();
            rsa.ImportFromPem(Encoding.UTF8.GetString(Convert.FromBase64String(capturedPubKey!)));
            var wrapped = rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);

            var iv = RandomNumberGenerator.GetBytes(16);
            var ct = InteractshClient.AesCtrTransform(aesKey, iv, Encoding.UTF8.GetBytes(interaction));
            var item = Convert.ToBase64String([.. iv, .. ct]);

            return Json(JsonSerializer.Serialize(new
            {
                aes_key = Convert.ToBase64String(wrapped),
                data = new[] { item },
            }));
        }));

        var results = await client.PollAsync(TestContext.Current.CancellationToken);

        var one = Assert.Single(results);
        Assert.Equal("dns", one.Protocol);
        Assert.Equal("203.0.113.7", one.RemoteAddress);
        Assert.Equal("abcnonce.oast.example.com", one.FullId);
        Assert.Equal("A", one.QType);
    }

    [Fact]
    public async Task PollAsync_reads_plaintext_extra_and_tld_data()
    {
        var client = new InteractshClient("https://oast.example.com", httpHandler: new StubHandler(req =>
            req.RequestUri!.AbsolutePath == "/register"
                ? Json("{}")
                : Json("""
                    {"extra":["{\"protocol\":\"http\",\"remote-address\":\"198.51.100.9\"}"],
                     "tld_data":["{\"protocol\":\"dns\",\"remote-address\":\"198.51.100.10\"}"]}
                    """)));

        var results = await client.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Protocol == "http" && r.RemoteAddress == "198.51.100.9");
        Assert.Contains(results, r => r.Protocol == "dns" && r.RemoteAddress == "198.51.100.10");
    }

    [Fact]
    public async Task PollAsync_returns_empty_when_nothing_called_back()
    {
        var client = new InteractshClient("https://oast.example.com",
            httpHandler: new StubHandler(_ => Json("""{"data":[]}""")));

        Assert.Empty(await client.PollAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PollAsync_sends_the_correlation_id_and_secret()
    {
        string? query = null;
        var client = new InteractshClient("https://oast.example.com", httpHandler: new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/poll") query = req.RequestUri.Query;
            return Json("{}");
        }));

        await client.PollAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(query);
        Assert.Contains("id=", query, StringComparison.Ordinal);
        Assert.Contains("secret=", query, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(route(request));
    }
}
