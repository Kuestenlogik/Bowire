// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Semantics;

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// Phase-5 helper that captures the effective annotation set into a
/// <see cref="BowireRecordingSchemaSnapshot"/> sidecar at the top of a
/// recording file. The sidecar is what the workbench uses to mount widgets
/// when a recording is opened — so a recording made against one user's
/// annotations renders identically for another user whose local
/// annotations differ.
/// </summary>
/// <remarks>
/// <para>
/// Pure transformation against an in-memory
/// <see cref="IAnnotationStore"/>: no disk I/O, no logging, no exceptions
/// thrown for unusual shapes (every annotation flows through as-is, since
/// the snapshot's role is to reproduce the workbench's resolver output
/// byte-for-byte). Filtering by <c>(service, method)</c> is optional —
/// callers that want the whole catalogue pass an empty filter.
/// </para>
/// </remarks>
public static class RecordingSchemaSnapshotBuilder
{
    /// <summary>
    /// Snapshot every effective annotation visible in <paramref name="store"/>
    /// that matches at least one of the supplied <c>(serviceId, methodId)</c>
    /// pairs. Pass an empty <paramref name="serviceMethodPairs"/> set to
    /// capture every effective annotation in the store regardless of
    /// service / method (rare — recordings usually pin themselves to a
    /// known set of methods).
    /// </summary>
    public static BowireRecordingSchemaSnapshot Build(
        IAnnotationStore store,
        IReadOnlyCollection<(string ServiceId, string MethodId)> serviceMethodPairs)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(serviceMethodPairs);

        var filter = serviceMethodPairs.Count == 0
            ? null
            : new HashSet<(string, string)>(serviceMethodPairs);

        var snapshot = new BowireRecordingSchemaSnapshot();
        foreach (var annotation in store.EnumerateEffective())
        {
            if (filter is not null &&
                !filter.Contains((annotation.Key.ServiceId, annotation.Key.MethodId)))
            {
                continue;
            }

            // Explicit suppression (None) is a real annotation value, not
            // the absence of one. The workbench widget router needs to
            // know about it on replay so a user-tier "none" suppresses a
            // lower-tier proposal exactly as it did at record-time.
            snapshot.Annotations.Add(new BowireRecordingSchemaAnnotation
            {
                Service = annotation.Key.ServiceId,
                Method = annotation.Key.MethodId,
                MessageType = annotation.Key.MessageType,
                JsonPath = annotation.Key.JsonPath,
                Semantic = annotation.Semantic.Kind,
            });
        }
        return snapshot;
    }
}
