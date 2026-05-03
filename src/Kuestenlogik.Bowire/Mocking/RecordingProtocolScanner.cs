// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// Pulls the distinct <c>protocol</c> IDs out of a loaded
/// <see cref="BowireRecording"/>. Used by <c>bowire mock</c> to
/// detect missing protocol plugins before the server boots — every
/// step references one protocol, and replay needs the matching
/// <c>IBowireProtocol</c> implementation registered in the host.
/// </summary>
public static class RecordingProtocolScanner
{
    /// <summary>
    /// Distinct, lowercased, non-empty protocol IDs across every step
    /// in <paramref name="recording"/>. Order is stable
    /// (first-occurrence) so error messages list protocols in the
    /// order they appear in the file.
    /// </summary>
    public static IReadOnlyList<string> Scan(BowireRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var step in recording.Steps)
        {
            var id = step.Protocol;
            if (string.IsNullOrWhiteSpace(id)) continue;
#pragma warning disable CA1308 // Protocol ids are case-insensitive but conventionally lowercase ("grpc", "signalr", "socketio") to match the Bowire protocol id convention.
            var normalised = id.Trim().ToLowerInvariant();
#pragma warning restore CA1308
            if (seen.Add(normalised)) ordered.Add(normalised);
        }
        return ordered;
    }
}
