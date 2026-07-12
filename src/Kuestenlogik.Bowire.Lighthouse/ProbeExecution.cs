// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Lighthouse;

/// <summary>
/// The raw result of running a probe's recording once, before assertions are
/// applied. Produced by an <see cref="IProbeExecutor"/>.
/// </summary>
public sealed record ProbeExecutionResult(int Status, double LatencyMs, string? Body);

/// <summary>
/// Executes a probe's saved recording and returns the raw response. The concrete
/// implementation replays the recording through the invoke path; the seam keeps
/// the scheduler + runner testable without a live target.
/// </summary>
/// <remarks>
/// A run that can't complete (transport error, timeout, DNS failure, …) is
/// signalled by throwing <see cref="ProbeExecutionException"/> — the runner
/// records it as an <see cref="ProbeResult.Error"/> outcome. Wrapping the
/// unbounded 3rd-party transport surface in one dedicated exception lets the
/// runner catch precisely that, rather than a general catch-all: an unexpected
/// exception (a genuine bug) still propagates instead of being silently logged
/// as a probe error.
/// </remarks>
public interface IProbeExecutor
{
    /// <summary>Run the probe's recording once against its bound target.</summary>
    Task<ProbeExecutionResult> ExecuteAsync(Probe probe, CancellationToken ct = default);
}

/// <summary>
/// Thrown by an <see cref="IProbeExecutor"/> when a probe run couldn't complete.
/// The runner catches this (and only this) to record an
/// <see cref="ProbeResult.Error"/> outcome.
/// </summary>
public sealed class ProbeExecutionException : Exception
{
    public ProbeExecutionException(string message) : base(message) { }
    public ProbeExecutionException(string message, Exception innerException) : base(message, innerException) { }
    public ProbeExecutionException() { }
}
