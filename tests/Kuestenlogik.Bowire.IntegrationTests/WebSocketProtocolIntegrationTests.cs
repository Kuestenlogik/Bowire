// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.WebSockets;
using System.Text;
using Kuestenlogik.Bowire.Protocol.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end tests for <see cref="BowireWebSocketProtocol"/> against a real
/// Kestrel host running an echo WebSocket. Verifies the channel API: open,
/// send a text frame, read the echoed response, close cleanly.
/// </summary>
public sealed class WebSocketProtocolIntegrationTests
{
    [Fact]
    public async Task OpenChannel_SendText_ReadsEchoedFrame()
    {
        await using var host = await PluginTestHost.StartAsync(MapEcho);
        var wsBaseUrl = host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal);

        var protocol = new BowireWebSocketProtocol();
        var channel = await protocol.OpenChannelAsync(
            wsBaseUrl + "/ws/echo",
            service: "WebSocket",
            method: "/ws/echo",
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(channel);
        await using (channel)
        {
            // Send a typed text frame the same shape the JS layer produces.
            var sent = await channel!.SendAsync("{\"type\":\"text\",\"text\":\"ping\"}", TestContext.Current.CancellationToken);
            Assert.True(sent);

            // Read the first response within a generous timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var envelope in channel.ReadResponsesAsync(cts.Token))
            {
                Assert.Contains("\"echo: ping\"", envelope, StringComparison.Ordinal);
                Assert.Contains("\"type\": \"text\"", envelope, StringComparison.Ordinal);
                break;
            }
        }
    }

    [Fact]
    public async Task OpenChannel_SendBinaryFrame_RoundTrips()
    {
        await using var host = await PluginTestHost.StartAsync(MapEcho);
        var wsBaseUrl = host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal);

        var protocol = new BowireWebSocketProtocol();
        var channel = await protocol.OpenChannelAsync(
            wsBaseUrl + "/ws/echo",
            service: "WebSocket",
            method: "/ws/echo",
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(channel);
        await using (channel)
        {
            // "BinaryTest" → base64
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("BinaryTest"));
            var sent = await channel!.SendAsync("{\"type\":\"binary\",\"base64\":\"" + payload + "\"}", TestContext.Current.CancellationToken);
            Assert.True(sent);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var envelope in channel.ReadResponsesAsync(cts.Token))
            {
                // Echo server returns binary frames as-is — the channel surfaces
                // them as { type: "binary", bytes, base64 } envelopes.
                Assert.Contains("\"type\": \"binary\"", envelope, StringComparison.Ordinal);
                Assert.Contains(payload, envelope, StringComparison.Ordinal);
                break;
            }
        }
    }

    /// <summary>
    /// Echo WebSocket: text frames come back prefixed with "echo: ", binary
    /// frames are returned unchanged. Mirrors the SimpleWebSocket sample.
    /// </summary>
    private static void MapEcho(WebApplication app)
    {
        app.Map("/ws/echo", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[8 * 1024];

            while (ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(buffer, ctx.RequestAborted);
                }
                catch (WebSocketException)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ctx.RequestAborted);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var reply = Encoding.UTF8.GetBytes("echo: " + text);
                    await ws.SendAsync(reply, WebSocketMessageType.Text, endOfMessage: true, ctx.RequestAborted);
                }
                else
                {
                    // Echo binary verbatim
                    await ws.SendAsync(buffer.AsMemory(0, result.Count), WebSocketMessageType.Binary, endOfMessage: true, ctx.RequestAborted);
                }
            }
        });
    }
}
