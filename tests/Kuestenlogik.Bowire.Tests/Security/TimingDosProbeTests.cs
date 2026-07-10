// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the two active timing-based DoS probes (#398): WebSocket
/// slow-loris (idle-timeout enforcement) and SSE slow-consumption (slow-reader
/// drop vs. unbounded feed). Driven through fakes with deterministic
/// close/no-close and end/keep-feeding behaviour so the verdict logic is tested
/// without depending on real server timing.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The probe under test owns channel disposal.")]
public sealed class TimingDosProbeTests
{
    private static readonly string[] s_auth = ["Authorization: Bearer x"];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static ActiveScanOptions Dur(int s) => new() { DurationSeconds = s };

    // ---------- WebSocket slow-loris ----------

    [Fact]
    public async Task WsSlowLoris_NonWsTarget_Silent()
        => Assert.Empty(await new WebSocketSlowLorisProbe().RunAsync("https://x", new WsFake(), s_auth, Dur(1), Ct));

    [Fact]
    public async Task WsSlowLoris_ServerClosesIdle_ReportsTimeout()
    {
        var fake = new WsFake { Channel = new WsChannelFake { CloseImmediately = true } };
        var f = Assert.Single(await new WebSocketSlowLorisProbe().RunAsync("ws://x/ws", fake, s_auth, Dur(5), Ct));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("SLOWLORIS-TIMEOUT", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WsSlowLoris_ConnectionHeld_FlagsSlowLoris()
    {
        var fake = new WsFake { Channel = new WsChannelFake { CloseImmediately = false } };
        var f = Assert.Single(await new WebSocketSlowLorisProbe().RunAsync("ws://x/ws", fake, s_auth, Dur(1), Ct));
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API4-WS-SLOWLORIS", f.Template.Recording.Vulnerability?.Id);
    }

    [Fact]
    public async Task WsSlowLoris_UpgradeRefused_Inconclusive()
    {
        var fake = new WsFake { Channel = null };
        var f = Assert.Single(await new WebSocketSlowLorisProbe().RunAsync("ws://x/ws", fake, s_auth, Dur(1), Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("SLOWLORIS-INCONCLUSIVE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    // ---------- SSE slow-consumption ----------

    [Fact]
    public async Task SseSlowConsume_NonHttpTarget_Silent()
        => Assert.Empty(await new SseSlowConsumptionProbe().RunAsync("mqtt://x", new SseFake(), s_auth, Dur(1), Ct));

    [Fact]
    public async Task SseSlowConsume_ServerKeepsFeeding_FlagsSlowConsume()
    {
        var fake = new SseFake { Infinite = true };
        var f = Assert.Single(await new SseSlowConsumptionProbe().RunAsync("https://x/events", fake, s_auth, Dur(1), Ct));
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API4-SSE-SLOWCONSUME", f.Template.Recording.Vulnerability?.Id);
    }

    [Fact]
    public async Task SseSlowConsume_ServerEndsStream_ReportsDropped()
    {
        var fake = new SseFake { EventCount = 1 };
        var f = Assert.Single(await new SseSlowConsumptionProbe().RunAsync("https://x/events", fake, s_auth, Dur(5), Ct));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("SLOWCONSUME-DROPPED", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SseSlowConsume_NoEvents_Skips()
    {
        var fake = new SseFake { EventCount = 0 };
        var f = Assert.Single(await new SseSlowConsumptionProbe().RunAsync("https://x/events", fake, s_auth, Dur(2), Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("SLOWCONSUME-NO-STREAM", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SseSlowConsume_StreamThrows_Inconclusive()
    {
        var fake = new SseFake { Throw = true };
        var f = Assert.Single(await new SseSlowConsumptionProbe().RunAsync("https://x/events", fake, s_auth, Dur(2), Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("SLOWCONSUME-INCONCLUSIVE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    // ---------- fakes ----------

    private sealed class WsFake : IBowireProtocol
    {
        public WsChannelFake? Channel { get; init; }
        public string Id => "websocket";
        public string Name => "websocket";
        public string IconSvg => "";
        public Task<List<BowireServiceInfo>> DiscoverAsync(string s, bool i, CancellationToken ct = default) => Task.FromResult(new List<BowireServiceInfo>());
        public Task<InvokeResult> InvokeAsync(string s, string sv, string m, List<string> j, bool i, Dictionary<string, string>? md = null, CancellationToken ct = default) => Task.FromResult(new InvokeResult(null, 0, "OK", []));
        public async IAsyncEnumerable<string> InvokeStreamAsync(string s, string sv, string m, List<string> j, bool i, Dictionary<string, string>? md = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<IBowireChannel?> OpenChannelAsync(string s, string sv, string m, bool i, Dictionary<string, string>? md = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(Channel);
    }

    private sealed class WsChannelFake : IBowireChannel
    {
        public bool CloseImmediately { get; init; }
        public string Id => "fake-ws";
        public bool IsClientStreaming => true;
        public bool IsServerStreaming => true;
        public int SentCount => 0;
        public bool IsClosed { get; private set; }
        public long ElapsedMs => 0;
        public Task<bool> SendAsync(string j, CancellationToken ct = default) => Task.FromResult(true);
        public Task CloseAsync(CancellationToken ct = default) { IsClosed = true; return Task.CompletedTask; }

        public async IAsyncEnumerable<string> ReadResponsesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            if (CloseImmediately) { await Task.CompletedTask; yield break; } // server closed the idle socket
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);    // hold open until the probe cancels
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SseFake : IBowireProtocol
    {
        public bool Infinite { get; init; }
        public int EventCount { get; init; }
        public bool Throw { get; init; }
        public string Id => "sse";
        public string Name => "sse";
        public string IconSvg => "";
        public Task<List<BowireServiceInfo>> DiscoverAsync(string s, bool i, CancellationToken ct = default) => Task.FromResult(new List<BowireServiceInfo>());
        public Task<InvokeResult> InvokeAsync(string s, string sv, string m, List<string> j, bool i, Dictionary<string, string>? md = null, CancellationToken ct = default) => Task.FromResult(new InvokeResult(null, 0, "OK", []));

        public async IAsyncEnumerable<string> InvokeStreamAsync(string s, string sv, string m, List<string> j, bool i, Dictionary<string, string>? md = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (Throw) throw new HttpRequestException("connection refused");
            if (Infinite)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return "{\"data\":\"tick\"}";
                }
            }
            for (var k = 0; k < EventCount; k++)
            {
                ct.ThrowIfCancellationRequested();
                yield return "{\"data\":\"tick\"}";
            }
        }

        public Task<IBowireChannel?> OpenChannelAsync(string s, string sv, string m, bool i, Dictionary<string, string>? md = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }
}
