// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// What each identity has chosen not to look at (#638).
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="BowireDisabledPluginsStore"/>, and the
/// distinction between them is the whole point. Disabling unloads a plugin
/// from the process, so it is one decision for everybody and its file lives in
/// the storage root. Hiding is a preference — "I don't use MQTT; stop showing
/// it to me" — so it lives in the identity's own slot and changes nothing
/// anyone else sees.
/// </para>
/// <para>
/// <b>Nothing on an execution path reads this.</b> Invoke, channel, discovery
/// and the MCP adapter all ignore it. Someone who hides a protocol and then
/// calls a method on it gets their result: hiding is tidying, not a
/// permission, and a preference that quietly breaks a request is a trap. Only
/// the listings the workbench renders carry it.
/// </para>
/// <para>
/// <b>Cached per resolved path, not per process.</b> One host serves many
/// identities, so a single static set would make the first person's choice
/// everybody's — which is exactly the defect #284 Phase D removed from the
/// disabled list. The path is resolved through
/// <see cref="BowireUserContext"/> on every call, so the entry a caller reads
/// is the one in their own slot.
/// </para>
/// </remarks>
public static class BowireHiddenProtocolsStore
{
    private const string FileName = "hidden-protocols.json";

    private static readonly Lock s_gate = new();

    // Keyed by resolved path, so two identities on one process keep two sets.
    // A plain dictionary: every read and write below holds s_gate, and one
    // rule is easier to keep than a lock and a concurrent collection that
    // each guard half of it.
    private static readonly Dictionary<string, HashSet<string>> s_byPath =
        new(StringComparer.Ordinal);

    // The written shape is the same minimal, hand-editable JSON the disabled
    // list uses: { "hidden": ["mqtt"] }.
    private static readonly JsonSerializerOptions s_persistOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// The ids the calling identity has hidden. A fresh set, so callers can
    /// iterate while somebody else writes.
    /// </summary>
    public static IReadOnlySet<string> Snapshot()
    {
        var path = ResolvePath();
        if (path is null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (s_gate)
        {
            return new HashSet<string>(Load(path), StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Whether the calling identity has hidden <paramref name="pluginId"/>.</summary>
    public static bool IsHidden(string pluginId)
    {
        if (string.IsNullOrEmpty(pluginId)) return false;

        var path = ResolvePath();
        if (path is null) return false;

        lock (s_gate)
        {
            return Load(path).Contains(pluginId);
        }
    }

    /// <summary>
    /// Hide or show <paramref name="pluginId"/> for the calling identity.
    /// Returns whether the set changed.
    /// </summary>
    /// <param name="pluginId">The protocol or plugin id.</param>
    /// <param name="hidden">True to hide, false to show again.</param>
    public static bool SetHidden(string pluginId, bool hidden)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);

        var path = ResolvePath();
        if (path is null) return false;

        lock (s_gate)
        {
            var set = Load(path);
            var changed = hidden ? set.Add(pluginId) : set.Remove(pluginId);
            if (changed) TryPersist(path, set);
            return changed;
        }
    }

    /// <summary>
    /// Test seam — drop every cached set so the next access reloads from disk.
    /// Files are not deleted; tests wanting a clean slate point the user store
    /// at a scratch directory.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (s_gate) { s_byPath.Clear(); }
    }

    /// <summary>
    /// The calling identity's file, or <c>null</c> when no slot can be
    /// resolved — an embedded host with an unusual store, most likely. A
    /// preference that cannot be saved degrades to "nothing hidden" rather
    /// than to an error: the workbench still works, it just shows everything.
    /// </summary>
    private static string? ResolvePath()
    {
        try { return BowireUserContext.GetUserPath(FileName); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _ = ex;
            return null;
        }
    }

    /// <summary>The cached set for <paramref name="path"/>. Callers hold s_gate.</summary>
    private static HashSet<string> Load(string path)
        => s_byPath.TryGetValue(path, out var cached)
            ? cached
            : s_byPath[path] = LoadFromDisk(path);

    private static HashSet<string> LoadFromDisk(string path)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return set;

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return set;
            if (!doc.RootElement.TryGetProperty("hidden", out var arr)) return set;
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
            // A corrupt or unreadable file yields an empty set. Showing a
            // protocol somebody hid is a visible annoyance; hiding one they
            // did not is a mystery, so this is the direction to fail in.
            _ = ex;
        }

        return set;
    }

    private static void TryPersist(string path, HashSet<string> set)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // An anonymous object with the lower-case member name, the same
            // way the disabled list writes its file: the wire shape is meant
            // to be diffed and hand-edited, so it does not go through a
            // naming policy that could drift.
            var payload = new
            {
                hidden = set.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, s_persistOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Best-effort, same as the disabled list: a read-only slot still
            // gets the change for this session.
            _ = ex;
        }
    }
}
