// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace Kuestenlogik.Bowire.Protocol.Rest;

/// <summary>
/// Process-wide ring buffer of OpenAPI auto-probe events.
/// Surfaces "which well-known path matched at &lt;origin&gt;" / "no
/// OpenAPI doc found at &lt;origin&gt;" so the workbench (and the
/// integration tests) can observe what the REST discovery plumbing
/// tried without wiring a full ILogger pipeline through every protocol
/// plugin. Bounded so a long-running tool doesn't grow this without
/// limit.
/// </summary>
/// <remarks>
/// Two log levels — Info for "we resolved &lt;origin&gt; → &lt;probe&gt;"
/// (operator-visible UX feedback) and Debug for "no OpenAPI document
/// found at &lt;origin&gt;" (quieter — the workbench surfaces an empty
/// service list anyway, but the log entry helps diagnose silent zero-
/// result discovery passes).
/// </remarks>
public static class RestProbeLog
{
    private const int Capacity = 128;
    private static readonly ConcurrentQueue<RestProbeLogEntry> _entries = new();

    /// <summary>Append an info-level entry (winning probe).</summary>
    public static void Info(string message)
        => Append(new RestProbeLogEntry(DateTimeOffset.UtcNow, RestProbeLogLevel.Info, message));

    /// <summary>Append a debug-level entry (no doc found / probe failure).</summary>
    public static void Debug(string message)
        => Append(new RestProbeLogEntry(DateTimeOffset.UtcNow, RestProbeLogLevel.Debug, message));

    /// <summary>Snapshot of recent entries (oldest → newest).</summary>
    public static IReadOnlyList<RestProbeLogEntry> Snapshot() => _entries.ToArray();

    /// <summary>Test helper — empties the buffer between cases.</summary>
    public static void Clear()
    {
        while (_entries.TryDequeue(out _)) { /* drain */ }
    }

    private static void Append(RestProbeLogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _)) { /* trim */ }
    }
}

/// <summary>Severity bucket for <see cref="RestProbeLogEntry"/>.</summary>
public enum RestProbeLogLevel { Debug, Info }

/// <summary>One entry in the REST probe ring buffer.</summary>
public sealed record RestProbeLogEntry(DateTimeOffset Timestamp, RestProbeLogLevel Level, string Message);
