// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// Process-wide cache of source-schema documents the wire plugins
/// captured at discovery time, keyed by the server URL the discovery
/// ran against. Lets the recording-save endpoint stamp
/// <see cref="BowireRecording.SourceSchema"/> automatically when the
/// workbench writes a new recording, without the JS recorder having
/// to know which plugin owns which URL.
/// </summary>
/// <remarks>
/// <para>
/// Population: each wire plugin writes one entry per call to its
/// <c>DiscoverAsync</c> that successfully loaded a source schema —
/// REST writes the verbatim OpenAPI text (JSON or YAML) keyed by the
/// URL the user pointed Bowire at; AsyncAPI writes the
/// <c>NormalisedYaml</c> the loader already preserves. Plugins that
/// have no source schema (gRPC, SignalR, the wire-driven MQTT/NATS/
/// Kafka discovery paths) simply don't write — the cache stays empty
/// for those URLs and the recording endpoint silently leaves
/// <c>SourceSchema</c> null.
/// </para>
/// <para>
/// Lookup: <c>BowireRecordingEndpoints</c> takes the first
/// step's <c>ServerUrl</c> from each recording being saved and
/// consults this cache. Found → stamp the recording's
/// <see cref="BowireRecording.SourceSchema"/>; missing → no-op.
/// </para>
/// <para>
/// Lifetime: process-wide, lives for the workbench host's runtime.
/// Cleared by <see cref="Clear"/> in tests; in production a fresh
/// discovery call simply overwrites the existing entry, which keeps
/// the cache consistent with the latest schema fetch.
/// </para>
/// </remarks>
public static class SourceSchemaCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RecordingSourceSchema> s_entries
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Store a source-schema entry for <paramref name="serverUrl"/>.
    /// Called by wire plugins during <c>DiscoverAsync</c>. Overwrites
    /// any prior entry for the same URL — the most recent discovery
    /// wins.
    /// </summary>
    public static void Set(string serverUrl, RecordingSourceSchema schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentNullException.ThrowIfNull(schema);
        s_entries[serverUrl] = schema;
    }

    /// <summary>
    /// Look up the source-schema entry for <paramref name="serverUrl"/>.
    /// Returns <c>null</c> when the URL hasn't been seen — recording
    /// enrichment treats that as "no schema available" and leaves
    /// <see cref="BowireRecording.SourceSchema"/> untouched.
    /// </summary>
    public static RecordingSourceSchema? Get(string? serverUrl)
    {
        if (string.IsNullOrEmpty(serverUrl)) return null;
        return s_entries.TryGetValue(serverUrl, out var schema) ? schema : null;
    }

    /// <summary>
    /// Drop every entry. Test-only; production callers shouldn't need
    /// this — a fresh discovery overwrites in place.
    /// </summary>
    public static void Clear() => s_entries.Clear();

    /// <summary>Number of entries currently cached. Test diagnostic.</summary>
    internal static int Count => s_entries.Count;
}
