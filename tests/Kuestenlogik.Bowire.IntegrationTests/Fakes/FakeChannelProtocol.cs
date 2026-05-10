// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Channels;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.IntegrationTests.Fakes;

/// <summary>
/// In-memory <see cref="IBowireProtocol"/> used by the channel-endpoint
/// happy-path integration tests. Auto-discovered via
/// <c>BowireProtocolRegistry.Discover</c> because the
/// IntegrationTests assembly name contains "Bowire". The id <c>"fake"</c>
/// is unique enough that no real plugin will ever collide with it.
///
/// <para>
/// All channel state lives in a <see cref="System.Threading.Channels.Channel{T}"/>
/// — sending pushes an echo response onto the queue, closing completes
/// it. That gives us full open/send/close/SSE coverage on
/// <see cref="Endpoints.BowireChannelEndpoints"/> without spinning up a
/// real gRPC or WebSocket listener.
/// </para>
/// </summary>
public sealed class FakeChannelProtocol : IBowireProtocol
{
    public string Name => "Fake (test)";
    public string Id => "fake";
    public string IconSvg => string.Empty;

    public Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
        => Task.FromResult(new List<BowireServiceInfo>());

    public Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        => Task.FromResult(new InvokeResult(null, 0, "OK", []));

#pragma warning disable CS1998 // async without await — empty enumerable
    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield break;
    }
#pragma warning restore CS1998

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => Task.FromResult<IBowireChannel?>(new FakeChannel());
}

internal sealed class FakeChannel : IBowireChannel
{
    private readonly Channel<string> _responses = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly DateTimeOffset _opened = DateTimeOffset.UtcNow;
    private int _sentCount;
    private bool _closed;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public bool IsClientStreaming => true;
    public bool IsServerStreaming => true;
    public int SentCount => _sentCount;
    public bool IsClosed => _closed;
    public long ElapsedMs => (long)(DateTimeOffset.UtcNow - _opened).TotalMilliseconds;
    public string? NegotiatedSubProtocol => null;

    public async Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default)
    {
        if (_closed) return false;
        _sentCount++;
        await _responses.Writer.WriteAsync($"{{\"echo\":{jsonMessage}}}", ct);
        return true;
    }

    public Task CloseAsync(CancellationToken ct = default)
    {
        if (!_closed)
        {
            _closed = true;
            _responses.Writer.TryComplete();
        }
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<string> ReadResponsesAsync(CancellationToken ct = default)
        => _responses.Reader.ReadAllAsync(ct);

    public ValueTask DisposeAsync()
    {
        if (!_closed) _responses.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
