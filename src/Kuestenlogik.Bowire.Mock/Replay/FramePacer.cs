// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mock.Replay;

/// <summary>
/// Holds a streaming replay back so frames arrive at the cadence they were
/// captured at.
/// </summary>
/// <remarks>
/// <para>
/// The same nine lines were written out nine times in
/// <see cref="UnaryReplayer"/> — once per streaming path — and seven of the
/// nine were reached by no test at all, because the suites replay at speed 0
/// where there is nothing to wait for. Nine copies of a rule is nine places
/// for it to come apart, and a test per copy would have entrenched that
/// rather than fixed it. Same reasoning as
/// <see cref="Chaos.FrameBudget"/>, which came out of the same file for the
/// same reason.
/// </para>
/// <para>
/// The cursor lives here rather than in each loop: "when was the last frame
/// due" is the pacer's business, and a caller that has to remember to
/// advance it is a caller that will forget.
/// </para>
/// <para>
/// The wait itself is a parameter so a test can read the interval that was
/// asked for instead of timing one. Asserting on a clock is how the first
/// version of those tests was written, and it failed under load — the same
/// mistake this file's own subject is about.
/// </para>
/// </remarks>
internal sealed class FramePacer(double speed, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay =
        delay ?? ((span, ct) => Task.Delay(span, ct));

    private long _lastTimestampMs;

    /// <summary>
    /// Whether this replay paces at all. <c>0</c> means "emit every frame
    /// immediately", which is what the test suites and
    /// <c>--replay-speed 0</c> ask for.
    /// </summary>
    private bool Paces => speed > 0;

    /// <summary>
    /// Move the origin forward to <paramref name="timestampMs"/>, for the
    /// paths where something other than a frame decides when "now" is.
    /// </summary>
    /// <remarks>
    /// An input-gated replay waits for the client before releasing the next
    /// frame, so the gap to the frame after it is measured from the moment
    /// the gate opened — not from the replay start, which would bunch every
    /// remaining frame up behind a slow client. Forward only, for the same
    /// reason a frame out of order does not move it.
    /// </remarks>
    public void Advance(long timestampMs)
    {
        if (timestampMs > _lastTimestampMs) _lastTimestampMs = timestampMs;
    }

    /// <summary>
    /// Wait until <paramref name="frameTimestampMs"/> is due.
    /// </summary>
    /// <param name="frameTimestampMs">
    /// The frame's captured offset, or <c>null</c> for a frame that carries
    /// none — an older recording, or a path that never captured them.
    /// </param>
    /// <param name="ct">Cancelled when the client goes away.</param>
    /// <returns>
    /// <c>false</c> when the wait was cancelled, which is the caller's signal
    /// to stop emitting. <c>true</c> otherwise, including when there was
    /// nothing to wait for.
    /// </returns>
    /// <remarks>
    /// A frame whose timestamp is not after the previous one does not move
    /// the cursor. Recordings are not guaranteed monotonic — a merged
    /// send/receive timeline interleaves two clocks — and letting one
    /// out-of-order frame move the cursor forward would silently drop the
    /// pacing of every frame after it.
    /// </remarks>
    public async Task<bool> WaitForAsync(long? frameTimestampMs, CancellationToken ct)
    {
        if (!Paces) return true;
        if (frameTimestampMs is not long due || due <= _lastTimestampMs) return true;

        var waitMs = (long)((due - _lastTimestampMs) / speed);
        if (waitMs > 0)
        {
            try
            {
                await _delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        _lastTimestampMs = due;
        return true;
    }
}
