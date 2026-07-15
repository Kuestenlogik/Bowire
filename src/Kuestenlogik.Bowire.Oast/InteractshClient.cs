// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Oast;

/// <summary>
/// An <see cref="IOastClient"/> speaking the interactsh wire protocol, so
/// <c>--oast-server</c> points at any compatible instance — Küstenlogik's, a
/// self-hosted one, or a third party's. No third-party dependency: HttpClient
/// plus BCL crypto only.
/// </summary>
/// <remarks>
/// <para>The exchange, per the reference implementation:</para>
/// <list type="number">
/// <item>Generate an RSA keypair. <c>POST /register</c> with
/// <c>{PublicKey (base64 of the PEM), SecretKey (uuid), CorrelationID}</c>.</item>
/// <item>Plant <c>&lt;correlationId&gt;&lt;nonce&gt;.&lt;domain&gt;</c> hosts in probes —
/// 20 + 13 characters by default, the length the server slices on.</item>
/// <item><c>GET /poll?id=&amp;secret=</c> returns <c>aes_key</c> (the AES key,
/// RSA-OAEP/SHA-256 wrapped to our public key) and <c>data</c> (each item
/// AES-CTR encrypted, IV = the first 16 bytes). <c>extra</c> / <c>tld_data</c>
/// arrive as plaintext JSON.</item>
/// </list>
/// <para>
/// The server never learns the AES key's plaintext from us and we never send
/// the private key — the point of the RSA wrap is that a shared/hosted
/// instance cannot read interactions belonging to other sessions.
/// </para>
/// </remarks>
public sealed class InteractshClient : IOastClient
{
    // 20 + 13 = the 33-character payload the server slices a correlation id
    // out of. Changing either half silently breaks correlation.
    private const int CorrelationIdLength = 20;
    private const int NonceLength = 13;

    // The correlation id must survive a DNS label, so it is drawn from the
    // lowercase base32-hex alphabet the reference id generator emits.
    private const string CorrelationAlphabet = "0123456789abcdefghijklmnopqrstuv";

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Serialisation for the request bodies. The server's fields are
    /// PascalCase (<c>PublicKey</c> / <c>SecretKey</c> / <c>CorrelationID</c>),
    /// and <see cref="HttpClientJsonExtensions.PostAsJsonAsync{TValue}(HttpClient, Uri, TValue, JsonSerializerOptions, CancellationToken)"/>
    /// would otherwise apply the Web defaults and camelCase them — which the
    /// server rejects. Pinned explicitly so the wire shape can't drift.
    /// </summary>
    private static readonly JsonSerializerOptions s_wireJson = new();

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly RSA _rsa;
    private readonly string _correlationId;
    private readonly string _secret;
    private readonly Uri _serverUri;
    private bool _registered;

    /// <inheritdoc />
    public string ServerDomain { get; }

    /// <summary>
    /// Create a client for the interaction server at <paramref name="serverUrl"/>
    /// (e.g. <c>https://oast.example.com</c>). Pass <paramref name="httpHandler"/>
    /// to drive the exchange in tests without a live server.
    /// </summary>
    public InteractshClient(string serverUrl, HttpMessageHandler? httpHandler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"OAST server must be an http(s) URL, got '{serverUrl}'.", nameof(serverUrl));
        }
        _serverUri = uri;
        ServerDomain = uri.Host;

        _ownsHttp = httpHandler is null;
        _http = CreateClient(httpHandler);
        _rsa = RSA.Create(2048);
        _correlationId = RandomFromAlphabet(CorrelationAlphabet, CorrelationIdLength);
        _secret = Guid.NewGuid().ToString();
    }

    /// <summary>
    /// Register this session's public key with the server. Called once, lazily,
    /// by the first <see cref="PollAsync"/>; exposed so a caller can fail fast
    /// (e.g. a bad --oast-server URL) before running a whole scan.
    /// </summary>
    public async Task RegisterAsync(CancellationToken ct = default)
    {
        if (_registered) return;

        // The server wants the PEM text, base64'd again — not the raw DER.
        var pem = new string(PemEncoding.Write("PUBLIC KEY", _rsa.ExportSubjectPublicKeyInfo()));
        var body = new RegisterRequest
        {
            PublicKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(pem)),
            SecretKey = _secret,
            CorrelationID = _correlationId,
        };

        using var resp = await _http.PostAsJsonAsync(new Uri(_serverUri, "/register"), body, s_wireJson, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new OastException(string.Create(CultureInfo.InvariantCulture,
                $"OAST register failed: {(int)resp.StatusCode} {resp.ReasonPhrase} (server {_serverUri})."));
        }
        _registered = true;
    }

    /// <inheritdoc />
    public OastAllocation Allocate()
    {
        // <correlation-id><nonce>.<domain>. The nonce keeps each probe's host
        // distinct while the correlation id keeps them all pollable in one
        // session — so a callback is always attributable to one probe.
        Span<byte> raw = stackalloc byte[16];
        RandomNumberGenerator.Fill(raw);
        var nonce = Zbase32.Encode(raw, NonceLength);
        var host = _correlationId + nonce + "." + ServerDomain;
        return new OastAllocation(host, _correlationId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OastInteraction>> PollAsync(CancellationToken ct = default)
    {
        await RegisterAsync(ct).ConfigureAwait(false);

        var url = new Uri(_serverUri,
            $"/poll?id={Uri.EscapeDataString(_correlationId)}&secret={Uri.EscapeDataString(_secret)}");
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new OastException(string.Create(CultureInfo.InvariantCulture,
                $"OAST poll failed: {(int)resp.StatusCode} {resp.ReasonPhrase} (server {_serverUri})."));
        }

        var poll = await resp.Content.ReadFromJsonAsync<PollResponse>(s_json, ct).ConfigureAwait(false);
        if (poll is null) return [];

        var results = new List<OastInteraction>();

        // `data` is encrypted per-item under the session AES key; the key
        // itself only exists once the server has something to hand back.
        if (poll.Data is { Count: > 0 } && !string.IsNullOrEmpty(poll.AesKey))
        {
            var aesKey = _rsa.Decrypt(Convert.FromBase64String(poll.AesKey), RSAEncryptionPadding.OaepSHA256);
            foreach (var item in poll.Data)
            {
                var decoded = TryDecrypt(aesKey, item);
                if (decoded is not null) results.Add(decoded);
            }
        }

        // `extra` + `tld_data` are plaintext JSON by design.
        AddPlaintext(poll.Extra, results);
        AddPlaintext(poll.TldData, results);

        return results;
    }

    private static void AddPlaintext(IReadOnlyList<string>? items, List<OastInteraction> into)
    {
        if (items is null) return;
        foreach (var raw in items)
        {
            var parsed = TryParse(raw);
            if (parsed is not null) into.Add(parsed);
        }
    }

    /// <summary>
    /// AES-CTR with the IV prefixed to the ciphertext. .NET has no CTR
    /// primitive, so it is built from ECB over a counter block — the standard
    /// construction, and the only correct way to interop here (using CBC or
    /// GCM would silently produce garbage).
    /// </summary>
    private static OastInteraction? TryDecrypt(byte[] key, string base64Item)
    {
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(base64Item);
        }
        catch (FormatException)
        {
            return null;
        }
        if (blob.Length <= 16) return null;

        var plain = AesCtrTransform(key, blob.AsSpan(0, 16), blob.AsSpan(16));
        return TryParse(Encoding.UTF8.GetString(plain));
    }

    /// <summary>
    /// The AES-CTR keystream transform — the single place this assembly touches
    /// a raw block cipher.
    /// </summary>
    /// <remarks>
    /// CTR is not a mode .NET exposes, so it is constructed the standard way:
    /// ECB is used purely as the block-cipher primitive to encrypt successive
    /// counter blocks into a keystream, which is then XOR'd with the data. No
    /// data is ever ECB-encrypted, so the weakness CA5358 warns about (ECB
    /// leaking plaintext structure across blocks) does not apply. The mode is
    /// not a choice: interactsh encrypts with AES-CTR, and CBC/GCM here would
    /// silently decrypt to garbage.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5358:Review cipher mode usage with cryptography expert",
        Justification = "ECB is the block-cipher primitive used to build CTR (encrypting counter blocks into a keystream), not a mode applied to data. CTR is required for interactsh wire compatibility and .NET exposes no CTR mode.")]
    internal static byte[] AesCtrTransform(byte[] key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> input)
    {
        var data = input.ToArray();
        var output = new byte[data.Length];

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        var counter = iv.ToArray();
        var keystream = new byte[16];
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            encryptor.TransformBlock(counter, 0, 16, keystream, 0);
            var take = Math.Min(16, data.Length - offset);
            for (var i = 0; i < take; i++) output[offset + i] = (byte)(data[offset + i] ^ keystream[i]);
            IncrementCounter(counter);
        }
        return output;
    }

    /// <summary>Big-endian +1 over the whole 16-byte block, as CTR requires.</summary>
    private static void IncrementCounter(byte[] counter)
    {
        for (var i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0) break;
        }
    }

    private static OastInteraction? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OastInteraction>(json, s_json);
        }
        catch (JsonException)
        {
            // A malformed item must not sink the whole poll.
            return null;
        }
    }

    private static string RandomFromAlphabet(string alphabet, int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }
        return new string(chars);
    }

    // The single boundary helper that owns HttpClient/handler creation — CA2000
    // (handler ownership) and CA5399 (CRL) are the known false-positives on the
    // `disposeHandler: true` pattern, documented + suppressed here exactly as
    // ScanCommand.BuildHttpClient does, so the suppression stays in one place.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "HttpClient(handler, disposeHandler: true) takes ownership — the handler is disposed with the client, which this type disposes.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient is created without enabling CheckCertificateRevocationList",
        Justification = "CheckCertificateRevocationList is set explicitly on the self-created handler below.")]
    private static HttpClient CreateClient(HttpMessageHandler? injected)
    {
        if (injected is not null)
        {
            var injectedClient = new HttpClient(injected, disposeHandler: false);
            injectedClient.DefaultRequestHeaders.UserAgent.ParseAdd("bowire-oast");
            return injectedClient;
        }
        var handler = new HttpClientHandler { CheckCertificateRevocationList = true };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("bowire-oast");
        return client;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Best-effort deregister so a hosted instance isn't left holding the
        // session; never let teardown fail a scan that already produced its
        // findings.
        if (_registered)
        {
            try
            {
                using var _ = await _http.PostAsJsonAsync(
                    new Uri(_serverUri, "/deregister"),
                    new DeregisterRequest { CorrelationID = _correlationId, SecretKey = _secret },
                    s_wireJson)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Session will age out server-side.
            }
        }
        _rsa.Dispose();
        if (_ownsHttp) _http.Dispose();
    }

    private sealed class RegisterRequest
    {
        public string PublicKey { get; set; } = "";
        public string SecretKey { get; set; } = "";
        public string CorrelationID { get; set; } = "";
    }

    private sealed class DeregisterRequest
    {
        public string CorrelationID { get; set; } = "";
        public string SecretKey { get; set; } = "";
    }

    private sealed class PollResponse
    {
        [JsonPropertyName("aes_key")] public string? AesKey { get; set; }
        [JsonPropertyName("data")] public List<string>? Data { get; set; }
        [JsonPropertyName("extra")] public List<string>? Extra { get; set; }
        [JsonPropertyName("tld_data")] public List<string>? TldData { get; set; }
    }
}

/// <summary>Raised when the interaction server rejects a register / poll.</summary>
public sealed class OastException : Exception
{
    public OastException() { }
    public OastException(string message) : base(message) { }
    public OastException(string message, Exception innerException) : base(message, innerException) { }
}
