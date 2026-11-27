// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.WebSocket;
using Kuestenlogik.Bowire.Security.Scanner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// The WebSocket authentication probe against real Kestrel servers, with a
/// JWT credential (Kuestenlogik/Bowire#963, Bowire.VulnDb#51).
/// </summary>
/// <remarks>
/// <para>
/// Every server below refuses an anonymous upgrade, except the one that is
/// open to everybody, so the probe's first question gets the same answer from
/// all of them. They differ in what they do with the token that arrives: look
/// for one, take its algorithm from the token's own header, or verify it. The
/// first two accept a forged token; only the last is healthy.
/// </para>
/// <para>
/// A stub with canned answers would prove the decision logic and not that the
/// forged tokens actually travel on a real upgrade request.
/// </para>
/// </remarks>
public sealed class WebSocketAuthProbeIntegrationTests
{
    private static readonly byte[] s_key = Encoding.UTF8.GetBytes("bowire-integration-test-signing-key-32b");

    // A genuine HS256 token for the key the verifying servers hold.
    private static readonly string s_token = Sign("""{"sub":"alice","groups":["user"],"exp":4102444800}""");

    [Fact]
    public async Task APresenceOnlyCheck_AcceptsBothForgeries()
    {
        var findings = await ScanAsync(MapPresenceOnly);

        Assert.All(findings, f => Assert.Equal(ScanFindingStatus.Vulnerable, f.Status));
        Assert.Equal(
            ["BWR-OWASP-API2-WS-ALG-NONE", "BWR-OWASP-API2-WS-SIG"],
            findings.Select(f => f.Template.Recording.Vulnerability?.Id).Order(StringComparer.Ordinal));
        Assert.All(findings, f => Assert.Equal("CWE-347", f.Template.Recording.Vulnerability?.Cwe));
    }

    [Fact]
    public async Task TakingTheAlgorithmFromTheToken_AcceptsAlgNoneOnly()
    {
        // Verifies HS256 signatures correctly, but lets the token say which
        // algorithm it uses: alg:none walks in, a bad signature does not.
        var f = Assert.Single(await ScanAsync(MapAlgorithmFromToken));
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API2-WS-ALG-NONE", f.Template.Recording.Vulnerability?.Id);
    }

    [Fact]
    public async Task VerifyingTheSignature_IsSafe()
    {
        var f = Assert.Single(await ScanAsync(MapVerified));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("API2-WS-AUTH-ENFORCED", f.Template.Recording.Id, StringComparison.Ordinal);
        Assert.Contains("alg:none", f.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOpenSocket_IsTheAnonymousFindingAlone()
    {
        // No forgery is tried: when anybody gets in, a forged token getting in
        // says nothing more.
        var f = Assert.Single(await ScanAsync(MapEveryoneAccepted));
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API2-WS-NOAUTH", f.Template.Recording.Vulnerability?.Id);
    }

    [Fact]
    public async Task AnOpaqueToken_IsNotForged()
    {
        // Only a JWT has a header and a signature to forge.
        var f = Assert.Single(await ScanAsync(MapPresenceOnly, "Authorization: Bearer opaque-session-token"));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("not a JWT", f.Detail, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<ScanFinding>> ScanAsync(Action<WebApplication> map, string? authHeader = null)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await PluginTestHost.StartAsync(map);
        var wsUrl = host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/exec";
        return await new WebSocketAuthProbe().RunAsync(wsUrl, new BowireWebSocketProtocol(),
            [authHeader ?? $"Authorization: Bearer {s_token}"], ct);
    }

    // ---- servers ----

    // The defect of Bowire.VulnDb#51: the token is decoded, never verified.
    private static void MapPresenceOnly(WebApplication app)
        => MapExec(app, token => !string.IsNullOrEmpty(token));

    private static void MapAlgorithmFromToken(WebApplication app)
        => MapExec(app, token => token is not null && (HeaderAlg(token) == "none" || Verifies(token)));

    // The healthy case: the algorithm is pinned and the signature checked.
    private static void MapVerified(WebApplication app)
        => MapExec(app, token => token is not null && HeaderAlg(token) == "HS256" && Verifies(token));

    private static void MapEveryoneAccepted(WebApplication app)
        => MapExec(app, _ => true);

    private static void MapExec(WebApplication app, Func<string?, bool> accept)
    {
        app.UseWebSockets();
        app.Map("/ws/exec", async context =>
        {
            var auth = context.Request.Headers.Authorization.ToString();
            var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth["Bearer ".Length..].Trim() : null;
            if (!accept(token))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            // The probe connects and closes without sending a frame: the
            // server holds the socket until that close arrives (or the
            // connection drops), which is the handshake the probe waits on.
            try
            {
                await socket.ReceiveAsync(new byte[256], context.RequestAborted);
            }
            catch (WebSocketException)
            {
                // closed without a close frame
            }
        });
    }

    // ---- tokens ----

    private static string Sign(string payloadJson)
    {
        var header = B64("""{"alg":"HS256","typ":"JWT"}"""u8.ToArray());
        var payload = B64(Encoding.UTF8.GetBytes(payloadJson));
        var signature = B64(HMACSHA256.HashData(s_key, Encoding.ASCII.GetBytes(header + "." + payload)));
        return header + "." + payload + "." + signature;
    }

    private static bool Verifies(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        var expected = B64(HMACSHA256.HashData(s_key, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1])));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[2]));
    }

    private static string? HeaderAlg(string token)
    {
        try
        {
            using var doc = JsonDocument.Parse(UnB64(token.Split('.')[0]));
            return doc.RootElement.TryGetProperty("alg", out var alg) ? alg.GetString() : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string B64(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnB64(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }
}
