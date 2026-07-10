// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.WebSockets;
using Kuestenlogik.Bowire.Protocol.WebSocket;
using Kuestenlogik.Bowire.Security.Scanner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end proof for the active WebSocket compression-bomb probe (#397)
/// against real Kestrel servers that accept <c>permessage-deflate</c>
/// (<see cref="WebSocketAcceptContext.DangerousEnableCompression"/>). Two
/// deterministic servers: one that inflates the whole frame with no cap (the
/// Vulnerable path) and one that closes with 1009 once a decompressed-byte
/// threshold is crossed (the Safe path). The probe opens its own
/// permessage-deflate <c>ClientWebSocket</c>, so this exercises the real
/// negotiation + amplification, not a fake.
/// </summary>
public sealed class WebSocketCompressionBombProbeIntegrationTests
{
    private const int Threshold = 512 * 1024;

    [Fact]
    public async Task NoDecompressionCap_FlagsCompressionBomb()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await PluginTestHost.StartAsync(MapUncappedWebSocket);
        var wsUrl = host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/uncapped";

        var probe = new WebSocketCompressionBombProbe();
        var findings = await probe.RunAsync(wsUrl, new BowireWebSocketProtocol(), [], new ActiveScanOptions { DurationSeconds = 1 }, ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API4-WS-COMPRESSION-BOMB", f.Template.Recording.Vulnerability?.Id);
        Assert.Equal("CWE-409", f.Template.Recording.Vulnerability?.Cwe);
    }

    [Fact]
    public async Task CapsDecompressedSize_ReportsSafe()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await PluginTestHost.StartAsync(MapCappedWebSocket);
        var wsUrl = host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/capped";

        var probe = new WebSocketCompressionBombProbe();
        var findings = await probe.RunAsync(wsUrl, new BowireWebSocketProtocol(), [], new ActiveScanOptions { DurationSeconds = 5 }, ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("COMPRESSION-BOMB-CAPPED", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    // Accepts permessage-deflate and inflates the whole frame with no size cap —
    // it just drains receives and never closes. The probe sees the connection
    // survive the settle window → Vulnerable.
    private static void MapUncappedWebSocket(WebApplication app)
    {
        app.Map("/ws/uncapped", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = true });
            var buffer = new byte[16 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try { result = await ws.ReceiveAsync(buffer, ctx.RequestAborted); }
                catch (Exception) { break; }
                if (result.MessageType == WebSocketMessageType.Close) break;
                // Inflate the whole message, no cap — just discard the bytes.
            }
        });
    }

    // Accepts permessage-deflate but enforces a decompressed-byte cap: once a
    // message inflates past the threshold it closes with 1009 Message Too Big.
    // The probe sees the server close shortly after the bomb → Safe.
    private static void MapCappedWebSocket(WebApplication app)
    {
        app.Map("/ws/capped", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = true });
            var buffer = new byte[16 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                var received = 0;
                WebSocketReceiveResult result;
                var capped = false;
                do
                {
                    try { result = await ws.ReceiveAsync(buffer, ctx.RequestAborted); }
                    catch (Exception) { return; }
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    received += result.Count;
                    if (received > Threshold)
                    {
                        capped = true;
                        break;
                    }
                }
                while (!result.EndOfMessage);

                if (capped)
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "decompressed size cap", ctx.RequestAborted); }
                    catch (Exception) { /* client may already be gone */ }
                    return;
                }
            }
        });
    }
}
