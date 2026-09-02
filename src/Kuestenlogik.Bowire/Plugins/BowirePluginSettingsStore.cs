// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// Reads and writes the values behind <see cref="IBowireProtocol.Settings"/>
/// (#640).
/// </summary>
/// <remarks>
/// <para>
/// One file per workspace, at <c>plugin-settings.json</c> under the workspace
/// path, in the shape it is diffed in:
/// </para>
/// <code>
/// { "dis": { "probeDuration": "5" }, "mqtt": { "scanDuration": "10" } }
/// </code>
/// <para>
/// Values are strings on the wire and in the file. The schema declares a type,
/// but the browser sends what a form control produced, and a store that
/// re-interprets that on the way in has to agree with whatever the plugin
/// expects on the way out. Keeping them opaque here puts the one conversion at
/// the point that knows what the value means — the accessors on
/// <see cref="IBowirePluginSettings"/>.
/// </para>
/// <para>
/// Cached per resolved path. One host serves several workspaces, so a single
/// cache would hand one workspace's probe window to another — the shape of
/// defect #284 removed from the disabled-plugins list.
/// </para>
/// </remarks>
public sealed class BowirePluginSettingsStore : IBowirePluginSettings
{
    private const string FileName = "plugin-settings.json";

    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true };

    private readonly Lock _gate = new();

    private readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> _byPath =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string? GetValue(string pluginId, string key)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(key)) return null;

        var path = ResolvePath();
        if (path is null) return null;

        lock (_gate)
        {
            return Load(path).TryGetValue(pluginId, out var forPlugin)
                && forPlugin.TryGetValue(key, out var value)
                    ? value
                    : null;
        }
    }

    /// <summary>Every value set for the workspace in scope.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Snapshot()
    {
        var path = ResolvePath();
        if (path is null) return new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        lock (_gate)
        {
            return Load(path).ToDictionary(
                p => p.Key,
                p => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(p.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Set <paramref name="key"/> for <paramref name="pluginId"/>, or clear it
    /// when <paramref name="value"/> is null.
    /// </summary>
    /// <returns>Whether anything was written.</returns>
    /// <remarks>
    /// Clearing rather than storing an empty string, so "set back to the
    /// default" and "set to nothing" stay different states — the second is
    /// what a plugin would have to guess about.
    /// </remarks>
    public bool Set(string pluginId, string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var path = ResolvePath();
        if (path is null) return false;

        lock (_gate)
        {
            var all = Load(path);
            if (value is null)
            {
                if (!all.TryGetValue(pluginId, out var forPlugin) || !forPlugin.Remove(key)) return false;
                if (forPlugin.Count == 0) all.Remove(pluginId);
            }
            else
            {
                if (!all.TryGetValue(pluginId, out var forPlugin))
                {
                    forPlugin = new Dictionary<string, string>(StringComparer.Ordinal);
                    all[pluginId] = forPlugin;
                }

                if (forPlugin.TryGetValue(key, out var existing)
                    && string.Equals(existing, value, StringComparison.Ordinal))
                {
                    return false;
                }

                forPlugin[key] = value;
            }

            Persist(path, all);
            return true;
        }
    }

    /// <summary>
    /// Test seam — drop every cached workspace so the next read comes from
    /// disk.
    /// </summary>
    internal void ResetForTests()
    {
        lock (_gate) { _byPath.Clear(); }
    }

    /// <summary>
    /// The file for the workspace in scope, or <c>null</c> when none is —
    /// the CLI, a test, an embedded host that never adopted workspaces. A
    /// plugin then gets its declared default, which is what it got before
    /// this existed.
    /// </summary>
    private static string? ResolvePath()
    {
        if (BowirePluginSettingsScope.Current is not { } scope) return null;

        try
        {
            return BowireUserContext.GetWorkspacePath(scope.WorkspaceId, scope.StorageRoot, FileName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // A workspace id the resolver refuses. Falling back to defaults
            // beats refusing to discover anything.
            _ = ex;
            return null;
        }
    }

    /// <summary>The cached values for <paramref name="path"/>. Callers hold the gate.</summary>
    private Dictionary<string, Dictionary<string, string>> Load(string path)
        => _byPath.TryGetValue(path, out var cached) ? cached : _byPath[path] = LoadFromDisk(path);

    private static Dictionary<string, Dictionary<string, string>> LoadFromDisk(string path)
    {
        var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (!File.Exists(path)) return all;

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return all;

            foreach (var plugin in doc.RootElement.EnumerateObject())
            {
                if (plugin.Value.ValueKind != JsonValueKind.Object) continue;

                var forPlugin = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var setting in plugin.Value.EnumerateObject())
                {
                    // Tolerate a hand-edited file that used a JSON number or
                    // boolean where the store writes strings: refusing the
                    // whole file over a quoting detail would lose every other
                    // setting in it.
                    var text = setting.Value.ValueKind switch
                    {
                        JsonValueKind.String => setting.Value.GetString(),
                        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                            => setting.Value.GetRawText(),
                        _ => null,
                    };
                    if (text is not null) forPlugin[setting.Name] = text;
                }

                if (forPlugin.Count > 0) all[plugin.Name] = forPlugin;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable file means "nothing configured", so every plugin
            // falls back to its documented default. The alternative is a
            // workbench that will not discover anything because one file is
            // corrupt.
            _ = ex;
        }

        return all;
    }

    private static void Persist(string path, Dictionary<string, Dictionary<string, string>> all)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(all, s_write));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Best-effort, like the other stores: the in-memory value still
            // applies for this session.
            _ = ex;
        }
    }
}
