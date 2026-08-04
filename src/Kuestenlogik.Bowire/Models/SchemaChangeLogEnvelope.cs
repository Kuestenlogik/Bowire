// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Models;

/// <summary>
/// On-disk / on-wire shape of a workspace's schema-change log (#185):
/// the retained change entries plus the read watermark. "Unread" is
/// derived — an entry is unread when it is newer than
/// <see cref="LastReadAt"/> — so marking the log read is a single
/// timestamp write instead of a per-entry flag sweep.
/// </summary>
public sealed record SchemaChangeLogEnvelope
{
    /// <summary>Retained change entries, oldest first.</summary>
    public IReadOnlyList<SchemaChangeEntry> Entries { get; init; } = [];

    /// <summary>
    /// When the operator last opened the change log. Entries newer than
    /// this are "unread"; null means nothing was ever read.
    /// </summary>
    public DateTimeOffset? LastReadAt { get; init; }
}
