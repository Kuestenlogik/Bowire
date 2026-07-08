// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for <see cref="WebSocketResourceLimitProbe"/> — the WebSocket
/// message-size probe that rolls up to <c>API4:2023 — Unrestricted Resource
/// Consumption</c>. The probe opens a socket, sends one bounded oversized text
/// frame (≈ 1 MB) and reads the server's reaction; a close / error frame means
/// a cap is enforced (Safe), a silently-accepted frame means none is (Vulnerable).
/// Everything is driven through self-contained fakes so the verdict logic is
/// tested without a live WebSocket server.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The probe under test owns the channel's disposal; the test keeps the reference only to assert it was disposed.")]
public sealed class WebSocketResourceLimitProbeTests
{
    private static readonly string[] s_auth = ["Authorization: Bearer x"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OversizedFrame_ServerClosesWith1009_ReportsCapEnforced()
    {
        var channel = new FakeChannel(["{\"type\":\"close\",\"status\":1009,\"description\":\"Message Too Big\"}"]);
        var proto = new FakeProtocol { Open = _ => channel };
        var probe = new WebSocketResourceLimitProbe();

        var f = Assert.Single(await probe.RunAsync("wss://x", proto, s_auth, Ct));

        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("API4-WS-MSGCAP-ENFORCED", f.Template.Recording.Id, StringComparison.Ordinal);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task OversizedFrame_ServerErrorFrame_ReportsCapEnforced()
    {
        var channel = new FakeChannel(["{\"type\":\"error\",\"message\":\"frame too large\"}"]);
        var proto = new FakeProtocol { Open = _ => channel };
        var probe = new WebSocketResourceLimitProbe();

        var f = Assert.Single(await probe.RunAsync("wss://x", proto, s_auth, Ct));

        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("API4-WS-MSGCAP-ENFORCED", f.Template.Recording.Id, StringComparison.Ordinal);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task OversizedFrame_ServerEchoesThenCompletes_FlagsApi4()
    {
        var channel = new FakeChannel(["{\"type\":\"text\",\"text\":\"ok\",\"bytes\":2}"]);
        var proto = new FakeProtocol { Open = _ => channel };
        var probe = new WebSocketResourceLimitProbe();

        var f = Assert.Single(await probe.RunAsync("wss://x", proto, s_auth, Ct));

        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API4-WS-NOMSGCAP", f.Template.Recording.Vulnerability?.Id);
        Assert.Equal("API4-2023-RESOURCE", f.Template.Recording.Vulnerability?.OwaspApi);
        Assert.Equal("CWE-400", f.Template.Recording.Vulnerability?.Cwe);
        // The probe actually sent a large (~1 MB) frame, not a token payload.
        Assert.True(channel.SendCalled);
        Assert.True(channel.LastSentLength > 900_000, $"expected a ~1 MB send, got {channel.LastSentLength} bytes");
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task OpenReturnsNull_Skips()
    {
        var proto = new FakeProtocol { Open = _ => null };
        var probe = new WebSocketResourceLimitProbe();

        var f = Assert.Single(await probe.RunAsync("wss://x", proto, s_auth, Ct));

        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API4-WS-INCONCLUSIVE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenThrows_SkipsInconclusive()
    {
        var proto = new FakeProtocol { Open = _ => throw new HttpRequestException("connection refused") };
        var probe = new WebSocketResourceLimitProbe();

        var f = Assert.Single(await probe.RunAsync("wss://x", proto, s_auth, Ct));

        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API4-WS-INCONCLUSIVE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendRefused_SkipsInconclusive_AndDisposes()
    {
        var channel = new FakeChannel([]) { SendResult = false };
        var proto = new FakeProtocol { Open = _ => channel };
        var probe = new WebSocketResourceLimitProbe();

        var f = Assert.Single(await probe.RunAsync("wss://x", proto, s_auth, Ct));

        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API4-WS-INCONCLUSIVE", f.Template.Recording.Id, StringComparison.Ordinal);
        Assert.True(channel.Disposed);
    }

    // ---------- fakes ----------

    private sealed class FakeProtocol : IBowireProtocol
    {
        public string Id => "websocket";
        public string Name => "websocket";
        public string IconSvg => "";
        public required Func<Dictionary<string, string>?, IBowireChannel?> Open { get; init; }

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => throw new InvalidOperationException("Discover not used by this probe");

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new InvalidOperationException("Invoke not used by this probe");

        public async IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(Open(metadata));
    }

    private sealed class FakeChannel : IBowireChannel
    {
        private readonly string[] _responses;

        public FakeChannel(string[] responses) => _responses = responses;

        public bool SendResult { get; init; } = true;
        public bool SendCalled { get; private set; }
        public int LastSentLength { get; private set; }
        public bool Disposed { get; private set; }

        public string Id => "fake";
        public bool IsClientStreaming => true;
        public bool IsServerStreaming => true;
        public int SentCount { get; private set; }
        public bool IsClosed { get; private set; }
        public long ElapsedMs => 0;

        public Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default)
        {
            SendCalled = true;
            LastSentLength = jsonMessage.Length;
            SentCount++;
            return Task.FromResult(SendResult);
        }

        public Task CloseAsync(CancellationToken ct = default)
        {
            IsClosed = true;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ReadResponsesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var env in _responses)
            {
                ct.ThrowIfCancellationRequested();
                yield return env;
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
