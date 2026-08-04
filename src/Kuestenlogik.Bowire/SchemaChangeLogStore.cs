// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Disk-backed store for a workspace's schema-change log (#185). The
/// schema watch (#48) diffs two discovery results in the browser;
/// this store is what makes the result durable — the client posts
/// each poll's delta here so "what changed while I was at lunch"
/// survives a reload, a browser reset, and other clients of the same
/// workspace. Entries older than <see cref="RetentionDays"/> are
/// pruned on every write and filtered on every read.
/// </summary>
/// <remarks>
/// Layout: one file per workspace at
/// <c>workspaces/&lt;wsId&gt;/schema-changes/log.json</c>, resolved
/// through <see cref="BowireUserContext.GetWorkspacePath"/> so the
/// per-identity / per-storage-root seams (#28, #212) keep working.
/// Registered as a DI singleton (not a static class like the older
/// stores) — the instance owns its file lock and test-path override.
/// </remarks>
public sealed partial class SchemaChangeLogStore
{
    /// <summary>How long a change entry is retained.</summary>
    public const int RetentionDays = 7;

    /// <summary>
    /// Hard cap on retained entries, newest win. A watch polling a
    /// schema under heavy churn every 5 s could otherwise grow the
    /// file without bound inside the retention window.
    /// </summary>
    public const int MaxEntries = 500;

    /// <summary>
    /// Per-string byte diet. A Detail is a one-line human summary and
    /// service/method names are identifiers — anything longer is
    /// truncated so a single client can't balloon the log file (which
    /// is re-read and re-written under the lock on every append, and
    /// shipped to every client on boot).
    /// </summary>
    public const int MaxNameLength = 300;

    /// <summary>See <see cref="MaxNameLength"/>.</summary>
    public const int MaxDetailLength = 500;

    /// <summary>
    /// Two clients watching the same workspace both observe — and both
    /// post — the same schema transition. An incoming entry that
    /// matches an existing one (type + service + method + detail)
    /// stamped within this window is dropped as a duplicate
    /// observation, not a second change.
    /// </summary>
    public static readonly TimeSpan DedupWindow = TimeSpan.FromMinutes(2);

    // CodeQL cs/path-injection allow-list — same anchored-regex
    // barrier PresetStore / ChunkedRecordingStore use, so the taint
    // on the user-supplied workspace id is dropped before it composes
    // into the on-disk path.
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeIdPattern();

    private static readonly JsonSerializerOptions FileJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly Lock _fileLock = new();
    private string? _testStorePathOverride;

    /// <summary>
    /// Pin the store to a fixed file for tests, bypassing the
    /// workspace-path resolution. Pass null to restore.
    /// </summary>
    public void OverrideStorePathForTesting(string? path)
    {
        _testStorePathOverride = path;
    }

    /// <summary>On-disk store location for a workspace.</summary>
    internal string GetStorePath(string workspaceId, string? storageRoot)
    {
        if (_testStorePathOverride is not null) return _testStorePathOverride;
        var safeWs = string.IsNullOrEmpty(workspaceId)
            ? string.Empty
            : SanitiseWorkspaceId(workspaceId);
        return BowireUserContext.GetWorkspacePath(
            safeWs,
            storageRoot,
            Path.Combine("schema-changes", "log.json"));
    }

    /// <summary>
    /// Load the change log, dropping entries outside the retention
    /// window. Returns the empty envelope when the file is missing or
    /// corrupt — never throws so the UI keeps working.
    /// </summary>
    public SchemaChangeLogEnvelope Load(string workspaceId, string? storageRoot)
    {
        lock (_fileLock)
        {
            return Prune(LoadUnlocked(workspaceId, storageRoot), DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Append a batch of change entries (one schema-watch poll's
    /// delta), prune the retention window, and persist. Returns the
    /// resulting envelope. Throws <see cref="ArgumentException"/> on
    /// a null entry, an unknown change type or a missing service so a
    /// malformed POST can't corrupt the on-disk log.
    /// </summary>
    /// <remarks>
    /// The server stamps <see cref="SchemaChangeEntry.At"/> on every
    /// entry, ignoring whatever the client sent. One clock authority
    /// keeps the retention window, the entry cap and the
    /// unread-vs-<see cref="SchemaChangeLogEnvelope.LastReadAt"/>
    /// compare consistent — a browser clock ahead of the server would
    /// otherwise produce entries that can never be marked read, and
    /// one behind would produce entries born read.
    /// </remarks>
    public SchemaChangeLogEnvelope Append(
        string workspaceId, string? storageRoot, IReadOnlyList<SchemaChangeEntry?> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var now = DateTimeOffset.UtcNow;
        var stamped = new List<SchemaChangeEntry>(entries.Count);
        foreach (var entry in entries)
        {
            // System.Text.Json happily materialises a JSON `null` array
            // element as a null entry — reject it as the 400 it is.
            if (entry is null)
                throw new ArgumentException("Change entry must not be null", nameof(entries));
            if (!SchemaChangeEntry.IsKnownType(entry.Type))
                throw new ArgumentException($"Unknown change type '{entry.Type}'", nameof(entries));
            if (string.IsNullOrWhiteSpace(entry.Service))
                throw new ArgumentException("Change entry requires a service", nameof(entries));
            stamped.Add(entry with
            {
                At = now,
                Service = Clip(entry.Service, MaxNameLength)!,
                Method = Clip(entry.Method, MaxNameLength),
                Detail = Clip(entry.Detail, MaxDetailLength),
            });
        }

        lock (_fileLock)
        {
            var current = Prune(LoadUnlocked(workspaceId, storageRoot), now);
            // Drop duplicate observations: a second client watching the
            // same workspace posts the same transition seconds later.
            var fresh = stamped.Where(e => !current.Entries.Any(existing =>
                existing.Type == e.Type
                && existing.Service == e.Service
                && existing.Method == e.Method
                && existing.Detail == e.Detail
                && now - existing.At <= DedupWindow)).ToList();
            var pruned = Prune(current with { Entries = [.. current.Entries, .. fresh] }, now);
            SaveUnlocked(workspaceId, storageRoot, pruned);
            return pruned;
        }
    }

    /// <summary>
    /// Move the read watermark to now — every current entry becomes
    /// "read". Returns the resulting envelope.
    /// </summary>
    public SchemaChangeLogEnvelope MarkRead(string workspaceId, string? storageRoot)
    {
        lock (_fileLock)
        {
            var now = DateTimeOffset.UtcNow;
            var updated = Prune(LoadUnlocked(workspaceId, storageRoot), now) with { LastReadAt = now };
            SaveUnlocked(workspaceId, storageRoot, updated);
            return updated;
        }
    }

    private SchemaChangeLogEnvelope LoadUnlocked(string workspaceId, string? storageRoot)
    {
        var path = GetStorePath(workspaceId, storageRoot);
        try
        {
            if (!File.Exists(path)) return new SchemaChangeLogEnvelope();
            var json = File.ReadAllText(path);
            var envelope = JsonSerializer.Deserialize<SchemaChangeLogEnvelope>(json, FileJsonOptions);
            return Normalise(envelope);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt or unreadable log is not worth breaking the
            // workbench over — the log restarts empty and refills.
            return new SchemaChangeLogEnvelope();
        }
    }

    /// <summary>
    /// A git-backed workspace's log file can be hand-edited or carry a
    /// merge-conflict resolution — JSON that parses fine but violates
    /// the shape (<c>"entries": null</c>, a <c>null</c> element). Bend
    /// it back into shape instead of letting a null dereference turn
    /// the workspace's change log into a permanent 500.
    /// </summary>
    private static SchemaChangeLogEnvelope Normalise(SchemaChangeLogEnvelope? envelope)
    {
        if (envelope is null) return new SchemaChangeLogEnvelope();
        // The property type says non-null, but an explicit JSON null
        // overrides the record's default — hence the null-resilient walk.
        var entries = (IReadOnlyList<SchemaChangeEntry?>?)envelope.Entries;
        if (entries is null) return envelope with { Entries = [] };
        if (entries.All(e => e is not null)) return envelope;
        return envelope with { Entries = [.. entries.OfType<SchemaChangeEntry>()] };
    }

    private void SaveUnlocked(string workspaceId, string? storageRoot, SchemaChangeLogEnvelope envelope)
    {
        var path = GetStorePath(workspaceId, storageRoot);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        // Write-then-rename so a reader in another process (embedded
        // host + standalone tool sharing a git-backed storageRoot) or
        // a crash mid-write can never observe a torn document — the
        // corrupt-file fallback above would otherwise "recover" it as
        // an empty log and the next append would persist the loss.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(envelope, FileJsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private static SchemaChangeLogEnvelope Prune(SchemaChangeLogEnvelope envelope, DateTimeOffset now)
    {
        var cutoff = now.AddDays(-RetentionDays);
        // The upper bound drops future-dated junk a pre-server-stamp
        // file (or a hand-edit) may carry: such an entry would never
        // age out, would always count as unread, and — sorting newest
        // under the cap — could evict every legitimate entry.
        var ceiling = now.AddMinutes(5);
        var kept = envelope.Entries.Where(e => e.At >= cutoff && e.At <= ceiling).ToList();
        if (kept.Count > MaxEntries)
        {
            kept = [.. kept.OrderBy(e => e.At)];
            kept.RemoveRange(0, kept.Count - MaxEntries);
        }
        return kept.Count == envelope.Entries.Count
            ? envelope
            : envelope with { Entries = kept };
    }

    private static string? Clip(string? value, int max)
    {
        if (value is null || value.Length <= max) return value;
        return value[..(max - 1)] + "…";
    }

    private static string SanitiseWorkspaceId(string workspaceId)
    {
        // Mirrors PresetStore.SanitiseWorkspaceId: strip everything
        // outside the safe character class, trim dots so `..` can't
        // escape upward, fall back to `anon`, then assert via the
        // anchored regex so CodeQL drops the taint.
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
