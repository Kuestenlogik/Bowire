// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.WebSockets;
using System.Text;
using Kuestenlogik.Bowire.Protocol.Sse;
using Kuestenlogik.Bowire.Protocol.WebSocket;
using Kuestenlogik.Bowire.Security.Scanner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end proof for the active timing-based DoS probes (#398), the
/// vulnerability ("held") path against real servers with no idle timeout /
/// no slow-reader drop — the deterministic case (the server never closes
/// within a short budget, so no timing flake). The Safe verdicts are covered
/// by the fast unit-fake tests.
/// </summary>
public sealed class TimingDosProbeIntegrationTests
{
    [Fact]
    public async Task WsSlowLoris_HeldConnection_FlagsNoIdleTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await PluginTestHost.StartAsync(MapHoldWebSocket);
        var wsUrl = host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/hold";

        var probe = new WebSocketSlowLorisProbe();
        var findings = await probe.RunAsync(wsUrl, new BowireWebSocketProtocol(), [], new ActiveScanOptions { DurationSeconds = 1 }, ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API4-WS-SLOWLORIS", f.Template.Recording.Vulnerability?.Id);
    }

    [Fact]
    public async Task SseSlowConsume_InfiniteStream_FlagsNoDrop()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await PluginTestHost.StartAsync(MapInfiniteSse);
        var sseUrl = host.BaseUrl + "/events";

        var probe = new SseSlowConsumptionProbe();
        var findings = await probe.RunAsync(sseUrl, new BowireSseProtocol(), [], new ActiveScanOptions { DurationSeconds = 1 }, ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API4-SSE-SLOWCONSUME", f.Template.Recording.Vulnerability?.Id);
    }

    // WebSocket endpoint that accepts the upgrade and then only reads — it never
    // closes an idle client, so the slow-loris probe holds it for the full budget.
    private static void MapHoldWebSocket(WebApplication app)
    {
        app.Map("/ws/hold", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[1024];
            while (ws.State == WebSocketState.Open)
            {
                try { await ws.ReceiveAsync(buffer, ctx.RequestAborted); }
                catch (Exception) { break; }
            }
        });
    }

    // SSE endpoint that streams forever (an event every 100ms) with no
    // slow-reader drop — the slow-consumption probe reads slowly and is fed for
    // the whole budget.
    private static void MapInfiniteSse(WebApplication app)
    {
        app.MapGet("/events", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                await ctx.Response.WriteAsync("data: {\"tick\":1}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                try { await Task.Delay(100, ctx.RequestAborted); }
                catch (Exception) { break; }
            }
        });
    }
}
