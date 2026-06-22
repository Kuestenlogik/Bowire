// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Disk-backed store for Bowire presets — named per-mode workflow
/// configurations (a saved Discover request with body + metadata,
/// a Benchmark profile, &amp;c). Pairs with the existing collections
/// store: collections group multiple presets, a preset is one
/// reusable setup. Persisting to disk (rather than relying on
/// browser localStorage as the framework originally did) lets
/// presets survive a browser reset, ride along with the workspace
/// export, and sync via git when the workspace is checked into a
/// repo.
/// </summary>
/// <remarks>
/// Layout: one file per (workspace, mode) at
/// <c>workspaces/&lt;wsId&gt;/presets/&lt;mode&gt;.json</c>, resolved
/// through <see cref="BowireUserContext.GetWorkspacePath"/> so the
/// per-identity / per-storage-root seams (#28, #212) keep working.
/// The on-disk shape is the raw JSON the workbench writes — an array
/// of preset records — so the endpoint is a pass-through.
/// </remarks>
internal static partial class PresetStore
{
    private static string? _testStorePathOverride;

    // CodeQL cs/path-injection allow-list. The recordings store
    // (ChunkedRecordingStore) routes every user-supplied id through
    // the same allow-list pattern + Regex.IsMatch barrier — anchored
    // regex against a character class is the form the analyser
    // recognises as a sanitiser, dropping the taint. The pattern
    // matches what's already used over there so the two stores stay
    // consistent.
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeIdPattern();

    /// <summary>
    /// On-disk store location for a given (workspace, mode) pair.
    /// Tests can pin via <see cref="OverrideStorePathForTesting"/> to
    /// redirect into a temp directory.
    /// </summary>
    internal static string GetStorePath(string workspaceId, string? storageRoot, string mode)
    {
        if (_testStorePathOverride is not null) return _testStorePathOverride;
        var safeMode = SanitiseMode(mode);
        // CodeQL cs/path-injection barrier — funnel workspaceId through
        // SanitiseWorkspaceId before it composes into the on-disk path.
        // Without this the file/directory operations in Load + Save
        // get flagged as path-injection sinks (#path-injection alerts
        // 1756/1757/1758/1759). Matches the recordings-store pattern.
        var safeWs = string.IsNullOrEmpty(workspaceId)
            ? string.Empty
            : SanitiseWorkspaceId(workspaceId);
        return BowireUserContext.GetWorkspacePath(
            safeWs,
            storageRoot,
            Path.Combine("presets", safeMode + ".json"));
    }

    internal static void OverrideStorePathForTesting(string? path)
    {
        _testStorePathOverride = path;
    }

    private static readonly Lock FileLock = new();

    private const string EmptyEnvelope = "[]";

    /// <summary>
    /// Load the raw JSON document. Returns the empty array shape
    /// when the file does not exist or is corrupt — never throws so
    /// the UI keeps working.
    /// </summary>
    public static string Load(string workspaceId, string? storageRoot, string mode)
    {
        var path = GetStorePath(workspaceId, storageRoot, mode);
        lock (FileLock)
        {
            try
            {
                if (!File.Exists(path)) return EmptyEnvelope;
                var json = File.ReadAllText(path);
                using var _ = JsonDocument.Parse(json);
                return json;
            }
            catch
            {
                return EmptyEnvelope;
            }
        }
    }

    /// <summary>
    /// Persist the JSON document verbatim, creating the parent
    /// directory on the way. Rejects invalid JSON so a corrupt PUT
    /// can't break the on-disk store.
    /// </summary>
    public static void Save(string workspaceId, string? storageRoot, string mode, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("JSON payload required", nameof(json));

        using var _ = JsonDocument.Parse(json);

        var path = GetStorePath(workspaceId, storageRoot, mode);
        lock (FileLock)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }
    }

    private static string SanitiseMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            throw new ArgumentException("Mode is required", nameof(mode));
        // Reject anything that isn't a simple ascii slug — the mode
        // becomes part of the filesystem path so we keep this
        // strictly allow-listed. The framework's known modes
        // (discover / flows / benchmarks / mocks / proxy / security)
        // all pass cleanly.
        foreach (var c in mode)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
            {
                throw new ArgumentException(
                    "Mode may only contain ascii letters, digits, '-' and '_'",
                    nameof(mode));
            }
        }
        // Anchored regex barrier — the analyser recognises this as the
        // canonical sanitisation pattern + drops the taint on the
        // returned value. The mode loop above guarantees a match.
        if (!SafeIdPattern().IsMatch(mode))
        {
            throw new ArgumentException(
                "Sanitised mode failed the path-safety allow-list: " + mode,
                nameof(mode));
        }
        return mode;
    }

    private static string SanitiseWorkspaceId(string workspaceId)
    {
        // Workspace ids are short slugs (`ws_<8 hex>` in the standard
        // generator + occasional manual ids). Defensive sanitisation
        // mirrors ChunkedRecordingStore.SanitiseId — strip everything
        // outside the safe character class, trim leading/trailing dots
        // so `..` can't escape upward, fall back to `anon` on empty,
        // then assert via the anchored regex so CodeQL drops the
        // taint.
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
