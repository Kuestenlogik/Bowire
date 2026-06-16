// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Workspace.Git;

/// <summary>
/// One file-system event surfaced by the workspace watcher. Emitted
/// on the SSE stream the workbench subscribes to so it can react to
/// external edits (a teammate's <c>git pull</c>, an editor saving an
/// environment file, etc.) without hammering the disk on a poll.
/// </summary>
/// <param name="Kind">
/// One of <c>file-created</c>, <c>file-changed</c>, <c>file-deleted</c>.
/// File renames are surfaced as a delete + create pair so the
/// downstream consumer doesn't need a separate rename arm.
/// </param>
/// <param name="RelativePath">
/// Path of the affected file relative to the watched workspace root.
/// Uses forward-slash separators on every OS so the JS-side payload
/// reads identically on Linux and Windows runners.
/// </param>
/// <param name="TimestampMs">
/// Server-side <see cref="DateTimeOffset.UtcNow"/> in unix-ms at the
/// moment the debounced event was emitted. Used by the SSE client to
/// dedup events that landed during a reconnect.
/// </param>
public readonly record struct WorkspaceFileEvent(
    string Kind,
    string RelativePath,
    long TimestampMs);
