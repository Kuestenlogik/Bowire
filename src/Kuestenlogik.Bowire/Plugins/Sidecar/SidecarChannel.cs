// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Kuestenlogik.Bowire.Plugins.Sidecar;

/// <summary>
/// <see cref="IBowireChannel"/> backed by a sidecar process over
/// JSON-RPC. Maps the duplex channel surface onto the wire:
/// <list type="bullet">
///   <item><c>channel.send</c> request per <see cref="SendAsync"/></item>
///   <item><c>channel.close</c> request on <see cref="CloseAsync"/></item>
///   <item><c>$/channel/data</c> notifications drive <see cref="ReadResponsesAsync"/></item>
///   <item><c>$/channel/closed</c> notification ends the read stream</item>
/// </list>
/// The channelId is host-generated and the host subscribes before the
/// <c>openChannel</c> request goes out (see
/// <see cref="SidecarBowireProtocol.OpenChannelAsync"/>), so the
/// subscription reader passed in is already live.
/// </summary>
internal sealed class SidecarChannel : IBowireChannel
{
    private readonly ISidecarTransport _transport;
    private readonly ChannelReader<JsonObject> _reader;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private int _sentCount;
    private bool _closed;
    private bool _disposed;

    public SidecarChannel(ISidecarTransport transport, string channelId, ChannelReader<JsonObject> reader)
    {
        _transport = transport;
        Id = channelId;
        _reader = reader;
    }

    public string Id { get; }

    // Sidecars don't currently declare per-channel direction flags over
    // the wire — the workbench treats every sidecar channel as full
    // duplex (the common case for the protocols that need a channel at
    // all: WebSocket, chat-style pub/sub).
    public bool IsClientStreaming => true;
    public bool IsServerStreaming => true;

    public int SentCount => _sentCount;
    public bool IsClosed => _closed;
    public long ElapsedMs => _sw.ElapsedMilliseconds;

    public async Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default)
    {
        if (_closed || _transport.HasExited) return false;
        try
        {
            await _transport.RequestAsync("channel.send", new
            {
                channelId = Id,
                message = jsonMessage,
            }, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _sentCount);
            return true;
        }
        catch (SidecarJsonRpcException)
        {
            return false;
        }
    }

    public async Task CloseAsync(CancellationToken ct = default)
    {
        if (_closed) return;
        _closed = true;
        if (_transport.HasExited) return;
        try
        {
            await _transport.RequestAsync("channel.close", new { channelId = Id }, ct)
                .ConfigureAwait(false);
        }
        catch (SidecarJsonRpcException) { /* sidecar already gone — fine */ }
    }

    public async IAsyncEnumerable<string> ReadResponsesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (await _reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_reader.TryRead(out var note))
            {
                var noteMethod = note["method"]?.GetValue<string>();
                var nparams = note["params"] as JsonObject;

                if (noteMethod == "$/channel/data")
                {
                    yield return SidecarBowireProtocol.ExtractMessage(nparams?["message"]);
                }
                else if (noteMethod == "$/channel/closed")
                {
                    _closed = true;
                    yield break;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await CloseAsync(cts.Token).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
        _transport.Unsubscribe(Id);
    }
}
