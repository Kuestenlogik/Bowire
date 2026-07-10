// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Unit coverage for the active WebSocket compression-bomb probe (#397). The
/// probe brings its own permessage-deflate <c>ClientWebSocket</c> rather than
/// driving the injected protocol, so the Safe / Vulnerable verdicts (which need
/// a live server) live in the integration suite. These deterministic cases
/// cover the two server-free branches: the wrong-scheme self-skip and the
/// can't-connect inconclusive marker.
/// </summary>
public sealed class WebSocketCompressionBombProbeTests
{
    private static readonly string[] s_auth = ["Authorization: Bearer x"];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static ActiveScanOptions Dur(int s) => new() { DurationSeconds = s };

    [Fact]
    public async Task NonWsTarget_Silent()
        => Assert.Empty(await new WebSocketCompressionBombProbe().RunAsync("https://x", protocol: null!, s_auth, Dur(1), Ct));

    [Fact]
    public async Task MqttTarget_Silent()
        => Assert.Empty(await new WebSocketCompressionBombProbe().RunAsync("mqtt://x:1883", protocol: null!, s_auth, Dur(1), Ct));

    [Fact]
    public async Task ConnectRefused_Inconclusive()
    {
        // A ws:// URL on a port that's guaranteed closed → ConnectAsync fails →
        // the probe reports an inconclusive Skipped marker rather than a verdict.
        var url = $"ws://127.0.0.1:{ClosedLoopbackPort()}/ws";
        var f = Assert.Single(await new WebSocketCompressionBombProbe().RunAsync(url, protocol: null!, s_auth, Dur(1), Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("COMPRESSION-BOMB-INCONCLUSIVE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    // Bind to an ephemeral loopback port, capture it, then release it — connecting
    // to the now-closed port reliably refuses (fast, no timing dependence).
    private static int ClosedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
