// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Disk-backed, id-addressable store for captured <see cref="AuthRecording"/>s
/// (#563 — the resolution half of #562's <c>authRecordingId</c> hook). Mirrors
/// <see cref="MockConfigStore"/>: one file per (workspace, recording) at
/// <c>workspaces/&lt;wsId&gt;/auth-recordings/&lt;id&gt;.json</c>, resolved
/// through <see cref="BowireUserContext.GetWorkspacePath"/> so the
/// per-identity / per-storage-root seams keep working, and persisting to disk
/// (not browser storage) lets a recording survive a browser reset, ride the
/// workspace export, and sync via git.
/// </summary>
internal static partial class AuthRecordingStore
{
    private static string? _testStorePathOverride;

    // CodeQL cs/path-injection allow-list — the same anchored pattern the
    // mock-config / preset / recordings stores use so a user-supplied id can't
    // escape the store directory. The anchored regex is the barrier form the
    // analyser recognises as a sanitiser.
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeIdPattern();

    private static readonly Lock FileLock = new();

    /// <summary>
    /// On-disk file for a given (workspace, recording) pair. Tests can pin via
    /// <see cref="OverrideStorePathForTesting"/> to redirect a single file into
    /// a temp directory.
    /// </summary>
    internal static string GetStorePath(string workspaceId, string? storageRoot, string recordingId)
    {
        if (_testStorePathOverride is not null) return _testStorePathOverride;
        var safeId = SanitiseRecordingId(recordingId);
        var safeWs = string.IsNullOrEmpty(workspaceId)
            ? string.Empty
            : SanitiseWorkspaceId(workspaceId);
        return BowireUserContext.GetWorkspacePath(
            safeWs,
            storageRoot,
            Path.Combine("auth-recordings", safeId + ".json"));
    }

    /// <summary>Directory holding a workspace's auth recordings (for <see cref="List"/>).</summary>
    internal static string GetStoreDirectory(string workspaceId, string? storageRoot)
    {
        var safeWs = string.IsNullOrEmpty(workspaceId)
            ? string.Empty
            : SanitiseWorkspaceId(workspaceId);
        return BowireUserContext.GetWorkspacePath(safeWs, storageRoot, "auth-recordings");
    }

    internal static void OverrideStorePathForTesting(string? path)
    {
        _testStorePathOverride = path;
    }

    /// <summary>
    /// Load a recording by id, or null when it does not exist or is corrupt —
    /// never throws so a callers's resolve path degrades cleanly.
    /// </summary>
    public static AuthRecording? LoadRecording(string workspaceId, string? storageRoot, string recordingId)
    {
        lock (FileLock)
        {
            try
            {
                // Path resolution is inside the try: an unsafe/empty id makes
                // GetStorePath throw, and the never-throws contract means that is
                // "no such recording" (null), not a propagated ArgumentException.
                var path = GetStorePath(workspaceId, storageRoot, recordingId);
                if (!File.Exists(path)) return null;
                return AuthRecording.Parse(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Persist a recording document. Rejects an empty payload, anything that
    /// is not a parseable <see cref="AuthRecording"/>, and a recording with no
    /// credential (a credential-less recording would silently weaken the gate
    /// to presence-only).
    /// </summary>
    public static void Save(string workspaceId, string? storageRoot, string recordingId, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("JSON payload required", nameof(json));

        // Validates JSON syntax + recording shape; throws JsonException on either.
        var parsed = AuthRecording.Parse(json);
        if (string.IsNullOrEmpty(parsed.Credential))
            throw new ArgumentException("An auth recording must carry a non-empty credential.", nameof(json));

        var path = GetStorePath(workspaceId, storageRoot, recordingId);
        lock (FileLock)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }
    }

    /// <summary>Typed convenience over <see cref="Save(string, string?, string, string)"/>.</summary>
    public static void Save(string workspaceId, string? storageRoot, AuthRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (string.IsNullOrEmpty(recording.Id))
            throw new ArgumentException("Recording id is required.", nameof(recording));
        Save(workspaceId, storageRoot, recording.Id, recording.ToJson());
    }

    /// <summary>
    /// List a workspace's recordings as credential-free summaries for the
    /// picker. Missing directory or unreadable files yield an empty list — never
    /// throws.
    /// </summary>
    public static IReadOnlyList<AuthRecordingSummary> List(string workspaceId, string? storageRoot)
    {
        lock (FileLock)
        {
            string[] files;
            try
            {
                // Directory resolution is inside the try too: a workspace id that
                // fails the path-safety sanitiser yields an empty list, per the
                // never-throws contract, rather than a propagated exception.
                var dir = GetStoreDirectory(workspaceId, storageRoot);
                if (!Directory.Exists(dir)) return [];
                files = Directory.GetFiles(dir, "*.json");
            }
            catch { return []; }

            var summaries = new List<AuthRecordingSummary>(files.Length);
            foreach (var file in files)
            {
                try
                {
                    var rec = AuthRecording.Parse(File.ReadAllText(file));
                    var id = string.IsNullOrEmpty(rec.Id)
                        ? Path.GetFileNameWithoutExtension(file)
                        : rec.Id;
                    var name = string.IsNullOrEmpty(rec.Name) ? id : rec.Name!;
                    summaries.Add(new AuthRecordingSummary(id, name, rec.Scheme, rec.CapturedAt));
                }
                catch
                {
                    // Skip a corrupt file rather than fail the whole listing.
                }
            }
            summaries.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return summaries;
        }
    }

    /// <summary>
    /// Delete a recording by id. Returns true when a file was removed, false
    /// when none existed. Never throws — an unsafe/empty id or an IO error is
    /// treated as "nothing to delete" (false).
    /// </summary>
    public static bool Delete(string workspaceId, string? storageRoot, string recordingId)
    {
        lock (FileLock)
        {
            try
            {
                var path = GetStorePath(workspaceId, storageRoot, recordingId);
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static string SanitiseRecordingId(string recordingId)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("Recording id is required", nameof(recordingId));
        // The id becomes part of the filesystem path — strip everything outside
        // the safe class, trim edge dots so `..` can't escape upward, then assert
        // via the anchored regex so CodeQL drops the taint.
        var sb = new StringBuilder(recordingId.Length);
        foreach (var c in recordingId.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.'))
        {
            sb.Append(c);
        }
        var result = sb.ToString().TrimStart('.').TrimEnd('.');
        if (string.IsNullOrEmpty(result) || !SafeIdPattern().IsMatch(result))
        {
            throw new ArgumentException(
                "Recording id must contain at least one ascii letter, digit, '-', '_' or '.': " + recordingId,
                nameof(recordingId));
        }
        return result;
    }

    private static string SanitiseWorkspaceId(string workspaceId)
    {
        var sb = new StringBuilder(workspaceId.Length);
        foreach (var c in workspaceId.Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.'))
        {
            sb.Append(c);
        }
        var result = sb.ToString().TrimStart('.').TrimEnd('.');
        if (string.IsNullOrEmpty(result)) result = "anon";

        if (!SafeIdPattern().IsMatch(result))
        {
            throw new ArgumentException(
                "Sanitised workspace id failed the path-safety allow-list: " + workspaceId,
                nameof(workspaceId));
        }
        return result;
    }
}
