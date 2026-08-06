// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Mock.Management;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// Adapter that lets the Mock package's config-apply endpoint resolve a
/// captured auth recording (#563) without a hard reference on the workbench's
/// stores. Standalone tool registers this at startup; embedded hosts plug
/// their own <see cref="IAuthRecordingResolver"/>.
/// </summary>
/// <remarks>
/// The config-apply endpoint doesn't carry a workspaceId (same limitation as
/// <see cref="WorkbenchRecordingJsonProvider"/>), so we discover the workspace
/// a recording belongs to by scanning every per-workspace
/// <c>auth-recordings/</c> directory — cheap for the typical 1-5 workspaces a
/// user has. First match by id wins.
/// </remarks>
internal sealed class WorkbenchAuthRecordingResolver : IAuthRecordingResolver
{
    public MockAuthResolution? TryResolve(string authRecordingId, string? workspaceId)
    {
        if (string.IsNullOrEmpty(authRecordingId)) return null;

        // Prefer the mock's own workspace when the caller knows it — an
        // auth-recording id is an operator-chosen label, so a bare id can exist
        // in more than one workspace; scoping avoids resolving to an arbitrary
        // one's credential.
        if (!string.IsNullOrEmpty(workspaceId))
        {
            if (TryLoad(workspaceId, authRecordingId) is { } scoped) return scoped;
        }

        // Fallback for callers that don't carry a workspace: scan every
        // workspace in a deterministic (ordinal-sorted) order, first match wins.
        foreach (var wsId in EnumerateWorkspaceIds())
        {
            if (TryLoad(wsId, authRecordingId) is { } hit) return hit;
        }
        return null;
    }

    private static MockAuthResolution? TryLoad(string workspaceId, string authRecordingId)
    {
        AuthRecording? rec;
        try { rec = AuthRecordingStore.LoadRecording(workspaceId, storageRoot: null, authRecordingId); }
        catch { return null; }
        return rec is not null && !string.IsNullOrEmpty(rec.Credential)
            ? new MockAuthResolution(rec.Credential, rec.Scheme, rec.Header)
            : null;
    }

    /// <summary>
    /// Workspace directories live under
    /// <c>BowireUserContext.GetUserPath("workspaces")</c>; each sub-directory is
    /// a workspace id. Missing parent or read errors yield an empty list so the
    /// resolver degrades to "not found" instead of throwing.
    /// </summary>
    private static IEnumerable<string> EnumerateWorkspaceIds()
    {
        string root;
        try { root = BowireUserContext.GetUserPath("workspaces"); }
        catch { yield break; }
        if (!Directory.Exists(root)) yield break;
        string[] entries;
        try { entries = Directory.GetDirectories(root); }
        catch { yield break; }
        // Sort so the scan fallback is deterministic across filesystems (raw
        // GetDirectories order is FS-dependent).
        Array.Sort(entries, StringComparer.Ordinal);
        foreach (var dir in entries)
        {
            var name = Path.GetFileName(dir);
            if (!string.IsNullOrWhiteSpace(name)) yield return name;
        }
    }
}
