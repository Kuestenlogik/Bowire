// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Protocol.Mcp;

/// <summary>
/// The wire order of one notification method on one client (#966). The SDK
/// starts every incoming message on a task of its own and does not await it
/// (<c>McpSessionHandler</c>: "fire and forget the message handling to avoid
/// blocking the transport"), so notification handlers run in no particular
/// order, and the response that ends a call can be handled before a
/// notification that preceded it on the wire. Logs carry no order of their
/// own, so it is recorded where it still exists: as
/// <see cref="McpWireOrderTransport"/> reads each message.
/// </summary>
/// <remarks>
/// Only notifications read after <see cref="Start"/> are numbered, so one
/// that arrived before its handler existed cannot leave a gap that nothing
/// will ever fill. A notification without a number is delivered at once.
/// </remarks>
internal sealed class McpWireOrder(string method)
{
    private readonly ConditionalWeakTable<JsonRpcMessage, StrongBox<long>> _positions = new();
    private readonly SortedDictionary<long, Action> _held = new();
    private readonly List<(long Upto, TaskCompletionSource Done)> _waiting = [];
    private readonly Lock _gate = new();
    private bool _started;
    private long _read;
    private long _delivered;

    /// <summary>Number the notifications read from now on; call it once their handler is registered.</summary>
    public void Start()
    {
        lock (_gate) _started = true;
    }

    /// <summary>The transport read <paramref name="message"/>; number it when it is one of ours.</summary>
    internal void Read(JsonRpcMessage message)
    {
        if (message is not JsonRpcNotification notification || notification.Method != method) return;
        lock (_gate)
        {
            if (!_started) return;
            _positions.AddOrUpdate(notification, new StrongBox<long>(_read++));
        }
    }

    /// <summary>
    /// Run <paramref name="emit"/> for <paramref name="notification"/> in wire
    /// order: now, if every earlier one has been delivered, otherwise once
    /// they have. <paramref name="emit"/> runs under a lock, so it must be
    /// short and must not block — a channel write.
    /// </summary>
    public void Deliver(JsonRpcNotification notification, Action emit)
    {
        if (!_positions.TryGetValue(notification, out var position))
        {
            emit();
            return;
        }
        lock (_gate)
        {
            _held[position.Value] = emit;
            while (_held.Remove(_delivered, out var next))
            {
                next();
                _delivered++;
            }
            for (var i = _waiting.Count - 1; i >= 0; i--)
            {
                if (_waiting[i].Upto > _delivered) continue;
                _waiting[i].Done.TrySetResult();
                _waiting.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Completes once every notification read so far has been delivered: what
    /// a call awaits before it writes its result, so a log the server sent
    /// first is not written after it, or lost once the stream has ended.
    /// </summary>
    public Task DeliveredAsync(CancellationToken ct)
    {
        TaskCompletionSource done;
        lock (_gate)
        {
            if (_delivered >= _read) return Task.CompletedTask;
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Add((_read, done));
        }
        return done.Task.WaitAsync(ct);
    }
}

/// <summary>
/// A client transport that tells an <see cref="McpWireOrder"/> about every
/// message in the order it is read, then hands it on unchanged (#966).
/// </summary>
internal sealed class McpWireOrderTransport(IClientTransport inner, McpWireOrder order) : IClientTransport, IAsyncDisposable
{
    public string Name => inner.Name;

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
        new Session(await inner.ConnectAsync(cancellationToken).ConfigureAwait(false), order);

    public ValueTask DisposeAsync() => inner is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;

    private sealed class Session : ITransport
    {
        private readonly ITransport _inner;
        private readonly McpWireOrder _order;
        private readonly Channel<JsonRpcMessage> _read = Channel.CreateUnbounded<JsonRpcMessage>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
        private readonly Task _pump;

        public Session(ITransport inner, McpWireOrder order)
        {
            _inner = inner;
            _order = order;
            _pump = PumpAsync();
        }

        public string? SessionId => _inner.SessionId;

        public ChannelReader<JsonRpcMessage> MessageReader => _read.Reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
            _inner.SendMessageAsync(message, cancellationToken);

        private async Task PumpAsync()
        {
            try
            {
                await foreach (var message in _inner.MessageReader.ReadAllAsync().ConfigureAwait(false))
                {
                    _order.Read(message);
                    _read.Writer.TryWrite(message);
                }
                _read.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                // The inner transport failed; the session sees it the same way.
                _read.Writer.TryComplete(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await _pump.ConfigureAwait(false);
        }
    }
}
