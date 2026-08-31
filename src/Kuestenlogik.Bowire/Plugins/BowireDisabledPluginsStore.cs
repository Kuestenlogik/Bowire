// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// Process-wide, disk-persisted store of plugin ids that have been
/// "unloaded" via the Settings → Plugins UI. Acts as the in-process
/// extension of <see cref="BowireOptions.DisabledPlugins"/>: the latter
/// carries the host-startup baseline (appsettings.json /
/// <c>--disable-plugin</c> CLI flag) which Bowire never rewrites because
/// it has no general-purpose appsettings.json writer; this store carries
/// the runtime layer that operators flip from the workbench UI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Process-wide is not a wording choice (#284 Phase D).</b> Unloading
/// re-runs <c>BowireProtocolRegistry.Discover</c> against the merged set and
/// swaps the registry every session reads, so what this file holds takes
/// effect for everybody on the host at once. It used to be written under
/// <c>&lt;user-store&gt;/</c>, which on a multi-tenant install gave each
/// identity their own copy of a list whose effect was shared — so the last
/// person to touch it decided for everyone, and nobody could see why.
/// </para>
/// <para>
/// It now resolves against <see cref="BowireStorageScope.Data"/>: the
/// process's own storage root, which is writable by the account Bowire runs
/// as and does not vary per caller. In single-user mode that is the very same
/// path as before — the flat layout puts a person's state directly in the
/// storage root — so nothing moves on a laptop. On a multi-tenant install the
/// old per-identity copies stop being read; they are left on disk rather than
/// merged, because a union of several people's lists is not any one person's
/// intent, and a protocol disabled by surprise is harder to notice than one
/// that is simply on.
/// </para>
/// <para>
/// What an individual wants <em>hidden in their own workbench</em> is a
/// different question with a different answer, and it does not live here.
/// </para>
/// <para>
/// State lives at <c>&lt;storage-root&gt;/disabled-plugins.json</c> so it
/// survives a host restart. The file is loaded into memory on first access.
/// Writes go straight to disk (best-effort: if the root is read-only, the
/// in-memory set still reflects the change for the current session).
/// </para>
/// <para>
/// IDs are matched case-insensitively against
/// <see cref="IBowireProtocol.Id"/>. The wire format is intentionally
/// minimal so operators can diff / hand-edit the file:
/// <code>
/// { "disabled": ["grpc", "mqtt"] }
/// </code>
/// </para>
/// </remarks>
public static class BowireDisabledPluginsStore
{
    private const string FileName = "disabled-plugins.json";

    private static readonly Lock s_gate = new();
    private static HashSet<string>? s_cached;

    // CA1869: persisting writes the same shape every time — cache the
    // serializer options so we don't reallocate per write.
    private static readonly JsonSerializerOptions s_persistOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Snapshot of currently-disabled plugin ids. Returns a fresh
    /// <see cref="HashSet{T}"/> so callers can iterate without
    /// worrying about concurrent mutation.
    /// </summary>
    public static IReadOnlySet<string> Snapshot()
    {
        EnsureLoaded();
        lock (s_gate)
        {
            return new HashSet<string>(s_cached!, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>True when <paramref name="pluginId"/> is in the disabled set.</summary>
    public static bool IsDisabled(string pluginId)
    {
        if (string.IsNullOrEmpty(pluginId)) return false;
        EnsureLoaded();
        lock (s_gate)
        {
            return s_cached!.Contains(pluginId);
        }
    }

    /// <summary>
    /// Add <paramref name="pluginId"/> to the disabled set and persist
    /// to disk. Returns true when the set changed (false when the id
    /// was already disabled).
    /// </summary>
    public static bool Disable(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        EnsureLoaded();
        lock (s_gate)
        {
            if (!s_cached!.Add(pluginId)) return false;
            TryPersist();
            return true;
        }
    }

    /// <summary>
    /// Remove <paramref name="pluginId"/> from the disabled set and
    /// persist to disk. Returns true when the set changed.
    /// </summary>
    public static bool Enable(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        EnsureLoaded();
        lock (s_gate)
        {
            if (!s_cached!.Remove(pluginId)) return false;
            TryPersist();
            return true;
        }
    }

    /// <summary>
    /// Combine the host-startup baseline (<paramref name="baseline"/>,
    /// typically <see cref="BowireOptions.DisabledPlugins"/>) with the
    /// runtime layer from this store. Used by the lifecycle endpoint
    /// when re-running discovery so a UI-triggered re-load doesn't
    /// silently un-disable a plugin the operator pinned in
    /// appsettings.json.
    /// </summary>
    public static IEnumerable<string> MergeWith(IEnumerable<string>? baseline)
    {
        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (baseline is not null)
        {
            foreach (var id in baseline)
            {
                if (!string.IsNullOrEmpty(id)) merged.Add(id);
            }
        }
        foreach (var id in Snapshot()) merged.Add(id);
        return merged;
    }

    /// <summary>
    /// Test seam — drop the in-memory cache so the next access reloads
    /// from disk. The on-disk file is not deleted; tests that need a clean
    /// slate point the storage root at a per-test scratch directory (an
    /// <c>IBowirePathResolver</c> in the container, or
    /// <c>BowirePaths.Current</c>).
    /// </summary>
    internal static void ResetForTests()
    {
        lock (s_gate) { s_cached = null; }
    }

    private static void EnsureLoaded()
    {
        // One check, under the lock. The double-checked version needed a
        // CA1508 suppression to survive — the analyzer cannot see the race
        // the inner check exists for — and this is a set of a dozen strings
        // read on endpoint paths, not a hot loop. Losing an uncontended
        // fast-path is cheaper than a suppression that has to be explained.
        lock (s_gate)
        {
            s_cached ??= LoadFromDisk();
        }
    }

    private static HashSet<string> LoadFromDisk()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string path;
        try { path = BowirePaths.Resolve(BowireStorageScope.Data, FileName); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The resolver rejected the file name or could not derive a
            // storage root. Fall back to an empty set; the lifecycle endpoint
            // still works in-process, it just does not persist.
            _ = ex;
            return set;
        }
        if (!File.Exists(path)) return set;
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return set;
            if (!doc.RootElement.TryGetProperty("disabled", out var arr)) return set;
            if (arr.ValueKind != JsonValueKind.Array) return set;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var id = item.GetString();
                if (!string.IsNullOrEmpty(id)) set.Add(id);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Best-effort: corrupt or unreadable file just yields an
            // empty set so the host still boots. Operator can re-export
            // the file from the workbench.
            _ = ex;
        }
        return set;
    }

    private static void TryPersist()
    {
        string path;
        try { path = BowirePaths.Resolve(BowireStorageScope.Data, FileName); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _ = ex;
            return;
        }
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            // Serialise the snapshot under lock — caller holds s_gate.
            var payload = new
            {
                disabled = s_cached!
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            };
            File.WriteAllText(path,
                JsonSerializer.Serialize(payload, s_persistOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Best-effort persistence. The in-memory set is already
            // updated; the next host restart will lose the change but
            // the current session reflects it.
            _ = ex;
        }
    }
}
