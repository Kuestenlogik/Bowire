// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Auth;

/// <summary>
/// Negotiate / NTLM / Digest (#679): the marker, how a handler is set up per
/// scheme, RFC 7616's own test vectors, and Digest end to end against a
/// server that checks every field — auth and auth-int, MD5 and SHA-256.
/// </summary>
public sealed class BowireHttpAuthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> Marker(object value) => new()
    {
        [BowireHttpAuth.MarkerKey] = System.Text.Json.JsonSerializer.Serialize(value),
        ["X-Trace"] = "1",
    };

    [Fact]
    public void The_Marker_Parses_And_Splits_Domain_From_User()
    {
        var config = BowireHttpAuth.TryParse(Marker(new { scheme = "ntlm", user = @"CORP\alice", password = "pw" }));
        Assert.NotNull(config);
        Assert.Equal(BowireHttpAuthScheme.Ntlm, config.Scheme);
        Assert.Equal("CORP", config.Domain);
        Assert.Equal("alice", config.User);
    }

    [Theory]
    [InlineData("{\"scheme\":\"basic\",\"user\":\"a\"}")]
    [InlineData("{\"scheme\":\"digest\"}")]
    [InlineData("not json")]
    public void An_Unusable_Marker_Is_Ignored(string json) =>
        Assert.Null(BowireHttpAuth.TryParse(new Dictionary<string, string> { [BowireHttpAuth.MarkerKey] = json }));

    [Fact]
    public void ApplyMetadata_Sends_Headers_Keeps_The_Credentials_Off_The_Wire()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://api.test/");
        BowireHttpAuth.ApplyMetadata(request, Marker(new { scheme = "digest", user = "alice", password = "pw" }));

        Assert.True(request.Headers.Contains("X-Trace"));
        Assert.DoesNotContain(request.Headers, h => h.Key.StartsWith("__bowire", StringComparison.OrdinalIgnoreCase));
        Assert.True(request.Options.TryGetValue(BowireHttpAuth.OptionKey, out var config));
        Assert.Equal("alice", config.User);
    }

    [Fact]
    public void Ntlm_Credentials_Are_Offered_To_Ntlm_And_Negotiate_Never_To_Basic()
    {
        using var handler = new HttpClientHandler();
        BowireHttpAuth.ApplyTo(handler, BowireHttpAuth.Parse("{\"scheme\":\"ntlm\",\"user\":\"CORP\\\\alice\",\"password\":\"pw\"}")!);

        var uri = new Uri("http://intranet.test/");
        Assert.Equal("alice", handler.Credentials!.GetCredential(uri, "NTLM")!.UserName);
        Assert.NotNull(handler.Credentials.GetCredential(uri, "Negotiate"));
        Assert.Null(handler.Credentials.GetCredential(uri, "Basic"));
        Assert.Null(handler.Credentials.GetCredential(uri, "Digest"));
    }

    [Fact]
    public void Negotiate_Uses_The_Signed_In_Account_Where_The_Host_Allows_It()
    {
        var config = BowireHttpAuth.Parse("{\"scheme\":\"negotiate\"}")!;
        using var handler = new HttpClientHandler();
        if (OperatingSystem.IsWindows())
        {
            BowireHttpAuth.ApplyTo(handler, config);
            Assert.True(handler.UseDefaultCredentials);
            Assert.True(BowireHttpAuth.Capabilities.Single(c => c.Id == "negotiate").Available);
        }
        else
        {
            var ex = Assert.Throws<PlatformNotSupportedException>(() => BowireHttpAuth.ApplyTo(handler, config));
            Assert.Contains("Windows", ex.Message, StringComparison.Ordinal);
            var capability = BowireHttpAuth.Capabilities.Single(c => c.Id == "negotiate");
            Assert.False(capability.Available);
            Assert.NotNull(capability.Reason);
        }
    }

    // RFC 7616 section 3.9.1 - the specification's own example.
    [Theory]
    [InlineData("MD5", "8ca523f5e9506fed4657c9700eebdbec")]
    [InlineData("SHA-256", "753927fa0e85d155564e2e272a28d1802ca10daf4496794697cf8db5856cb6c1")]
    public void The_Response_Matches_Rfc7616s_Example(string algorithm, string expected)
    {
        var challenge = DigestChallenge.Parse(
            "realm=\"http-auth@example.org\", qop=\"auth, auth-int\", algorithm=" + algorithm
            + ", nonce=\"7ypf/xlj9XXwfDPEoM4URrv/xwf94BcCAzFZH4GiTo0v\", opaque=\"FQhe/qaU925kfnzjCev0ciny7QMkPqMAFRtzCUYo5tdS\"")!;

        var header = BowireDigestHandler.Authorize(challenge, "GET", "/dir/index.html", null,
            "Mufasa", "Circle of Life", "f2/wE4q74E6zIJEtWaHKaf5wv/H5QzzpXusqGemxURZJ", 1);

        var fields = DigestChallenge.ParseParameters(header);
        Assert.Equal(expected, fields["response"]);
        Assert.Equal("auth", fields["qop"]);
        Assert.Equal("00000001", fields["nc"]);
        Assert.Equal("FQhe/qaU925kfnzjCev0ciny7QMkPqMAFRtzCUYo5tdS", fields["opaque"]);
    }

    [Theory]
    [InlineData("MD5", "auth")]
    [InlineData("SHA-256", "auth")]
    [InlineData("MD5", "auth-int")]
    [InlineData("SHA-256", "auth-int")]
    [InlineData("MD5-sess", "auth")]
    public async Task Digest_Succeeds_End_To_End(string algorithm, string qop)
    {
        await using var server = await DigestServer.StartAsync(algorithm, qop, "alice", "s3cret");
        using var client = BowireHttpClientFactory.Create(null, "rest", TimeSpan.FromSeconds(10));

        using var request = new HttpRequestMessage(HttpMethod.Post, server.Url + "/orders?id=7")
        {
            Content = new StringContent("{\"qty\":2}", Encoding.UTF8, "application/json"),
        };
        BowireHttpAuth.ApplyMetadata(request, Marker(new { scheme = "digest", user = "alice", password = "s3cret" }));
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"qty\":2}", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_Wrong_Digest_Password_Ends_In_401_Not_A_Loop()
    {
        await using var server = await DigestServer.StartAsync("MD5", "auth", "alice", "s3cret");
        using var client = BowireHttpClientFactory.Create(null, "rest", TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Url + "/orders");
        BowireHttpAuth.ApplyMetadata(request, Marker(new { scheme = "digest", user = "alice", password = "wrong" }));

        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, server.Requests);
    }

    [Fact]
    public async Task Without_The_Marker_The_Shared_Handler_Sends_No_Credentials()
    {
        await using var server = await DigestServer.StartAsync("MD5", "auth", "alice", "s3cret");
        using var client = BowireHttpClientFactory.Create(null, "rest", TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(new Uri(server.Url + "/orders"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, server.Requests);
    }

    /// <summary>A Digest-protected endpoint that checks the answer field by field, independently of Bowire's code.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351", Justification = "MD5 is what Digest specifies; this is the server side of the test.")]
    private sealed class DigestServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _requests;

        private DigestServer(WebApplication app) => _app = app;

        public string Url => _app.Urls.First();
        public int Requests => _requests;

        public static async Task<DigestServer> StartAsync(string algorithm, string qop, string user, string password)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var server = new DigestServer(app);
            const string realm = "orders@test";
            const string nonce = "dcd98b7102dd2f0e8b11d0f600bfb0c093";
            app.Run(async ctx =>
            {
                Interlocked.Increment(ref server._requests);
                ctx.Request.EnableBuffering();
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                var body = ms.ToArray();

                var auth = ctx.Request.Headers.Authorization.ToString();
                if (auth.StartsWith("Digest ", StringComparison.Ordinal))
                {
                    var f = DigestChallenge.ParseParameters(auth[7..]);
                    var sha = algorithm.StartsWith("SHA-256", StringComparison.Ordinal);
                    string H(string s) => Hex(sha ? SHA256.HashData(Encoding.UTF8.GetBytes(s)) : MD5.HashData(Encoding.UTF8.GetBytes(s)));
                    var ha1 = H($"{user}:{realm}:{password}");
                    if (algorithm.EndsWith("-sess", StringComparison.Ordinal)) ha1 = H($"{ha1}:{nonce}:{f["cnonce"]}");
                    var uri = ctx.Request.Path + ctx.Request.QueryString;
                    var ha2 = qop == "auth-int"
                        ? H($"{ctx.Request.Method}:{uri}:{Hex(sha ? SHA256.HashData(body) : MD5.HashData(body))}")
                        : H($"{ctx.Request.Method}:{uri}");
                    var expected = H($"{ha1}:{nonce}:{f["nc"]}:{f["cnonce"]}:{qop}:{ha2}");
                    if (f["username"] == user && f["realm"] == realm && f["nonce"] == nonce && f["uri"] == uri
                        && f["qop"] == qop && f["response"] == expected)
                    {
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.Body.WriteAsync(body.Length > 0 ? body : "{}"u8.ToArray());
                        return;
                    }
                }
                ctx.Response.StatusCode = 401;
                ctx.Response.Headers.WWWAuthenticate = $"Digest realm=\"{realm}\", qop=\"{qop}\", algorithm={algorithm}, nonce=\"{nonce}\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"";
            });
            await app.StartAsync(Ct);
            return server;
        }

        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
