// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mock.Chaos;

/// <summary>
/// Response-body wrapper behind <see cref="FaultKind.PartialResponse"/> /
/// <see cref="FaultKind.ConnectionDrop"/> (#170). Forwards at most
/// <c>capBytes</c> to the real response stream; once the cap is reached,
/// further writes are swallowed (partial-response: the response ends
/// cleanly with a truncated body) or the connection is aborted via the
/// supplied callback (connection-drop: client sees a reset mid-body).
/// The replayer stays completely unaware — it writes the full recorded
/// body as always.
/// </summary>
internal sealed class TruncatingResponseStream : Stream
{
    private readonly Stream _inner;
    private readonly Action? _abort;
    private long _remaining;
    private bool _tripped;

    public TruncatingResponseStream(Stream inner, long capBytes, Action? abortConnection)
    {
        _inner = inner;
        _remaining = capBytes;
        _abort = abortConnection;
    }

    /// <summary>True once the cap was hit at least once (for the audit trail).</summary>
    public bool Tripped => _tripped;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        var allowed = Clamp(count);
        if (allowed > 0) _inner.Write(buffer, offset, allowed);
        AfterWrite(count, allowed);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var allowed = Clamp(count);
        if (allowed > 0) await _inner.WriteAsync(buffer.AsMemory(offset, allowed), cancellationToken).ConfigureAwait(false);
        AfterWrite(count, allowed);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var allowed = Clamp(buffer.Length);
        if (allowed > 0) await _inner.WriteAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
        AfterWrite(buffer.Length, allowed);
    }

    // The ASP.NET pipeline never disposes a swapped-in Response.Body
    // wrapper itself, but a well-behaved wrapper forwards ownership if it
    // ever IS disposed (also satisfies CA2213 without a suppression).
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private int Clamp(int requested)
        => (int)Math.Min(requested, Math.Max(0, _remaining));

    private void AfterWrite(int requested, int allowed)
    {
        _remaining -= allowed;
        if (requested > allowed && !_tripped)
        {
            _tripped = true;
            _abort?.Invoke();
        }
    }
}
