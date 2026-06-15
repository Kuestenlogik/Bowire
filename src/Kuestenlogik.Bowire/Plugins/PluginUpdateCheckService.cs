// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// Reusable "check installed plugin versions against nuget.org"
/// pipeline shared between the daily background check
/// (<see cref="PluginUpdateCheckHostedService"/>) and the manual
/// <c>GET /api/plugins/check-updates</c> endpoint.
/// </summary>
/// <remarks>
/// Hits the NuGet v3-flatcontainer registration index for each
/// installed sibling plugin (bundled plugins are skipped — they
/// move with <c>dotnet tool update</c>, not as standalone packages).
/// Result snapshot is cached on disk under
/// <c>~/.bowire/state/update-check.json</c> so the UI can render
/// the last-known state without re-hitting nuget.org on every page
/// load.
/// </remarks>
public class PluginUpdateCheckService
{
    // Plugin dir stays on the legacy flat path -- the per-user-vs-
    // system-wide split is #28 Phase D ("Per-user plugin installs --
    // split ~/.bowire/plugins/ into a system-wide tier plus a per-user
    // overlay"), out of scope for this phase.
    private static readonly string DefaultPluginDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".bowire", "plugins");

    private static string? s_pluginDirOverride;

    /// <summary>
    /// On-disk plugin directory the scan walks. Defaults to the legacy
    /// <c>~/.bowire/plugins/</c> layout; tests pin a temp dir via the
    /// setter to redirect the scan without depending on whatever the
    /// developer happens to have installed locally. Same pattern as
    /// <c>RecordingStore.StorePath</c> / <c>CollectionStore.StorePath</c>.
    /// </summary>
    internal static string PluginDir
    {
        get => s_pluginDirOverride ?? DefaultPluginDir;
        set => s_pluginDirOverride = value;
    }

    // Cache for the daily update-check snapshot routes through the
    // IBowireUserStore seam (#28 Phase 2). Single-user installs land at
    // ~/.bowire/state/update-check.json (unchanged); multi-tenant
    // installs slot per-identity once Phase C wires the AsyncLocal
    // resolver. The path is computed lazily via the static accessor so
    // BowireUserContext can be swapped at startup before the first hit.
    // Re-using GetUserPath for the "state" sub-directory is a small
    // stretch of the seam's "filename" contract -- Path.Combine doesn't
    // care, and the alternative is a contract extension that's not
    // pulling its weight yet (one Phase-2 caller).
    private static string CachePath =>
        Path.Combine(BowireUserContext.GetUserPath("state"), "update-check.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public PluginUpdateCheckService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Run a live check against nuget.org for every installed sibling
    /// plugin. Result is persisted to <see cref="CachePath"/> on
    /// success so the next status read returns immediately.
    /// </summary>
    /// <remarks>
    /// Virtual so tests can derive + override to drive the
    /// <see cref="PluginUpdateCheckHostedService"/>'s exception-handling
    /// path without spinning a fake HTTP stack that throws mid-response.
    /// </remarks>
    public virtual async Task<PluginUpdateCheckSnapshot> CheckAsync(
        bool includePrerelease, CancellationToken ct)
    {
        var installed = ListInstalledSiblings();
        var results = new List<PluginUpdateCheckResult>(installed.Count);

        foreach (var (packageId, installedVersion) in installed)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var latest = await FetchLatestAsync(packageId, includePrerelease, ct);
                results.Add(new PluginUpdateCheckResult(
                    packageId,
                    installedVersion,
                    latest,
                    UpdateAvailable: latest is not null
                        && !string.Equals(latest, installedVersion, StringComparison.OrdinalIgnoreCase),
                    Error: null));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException or InvalidOperationException)
            {
                // FetchLatestAsync wraps NuGet flatcontainer HTTP calls.
                // OperationCanceledException intentionally NOT in the
                // filter — cancellation propagates up to the caller.
                results.Add(new PluginUpdateCheckResult(
                    packageId, installedVersion, Latest: null, UpdateAvailable: false, Error: ex.Message));
            }
        }

        var snapshot = new PluginUpdateCheckSnapshot(
            CheckedAt: DateTimeOffset.UtcNow,
            IncludePrerelease: includePrerelease,
            Results: results);
        await WriteCacheAsync(snapshot, ct).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>
    /// Read the last persisted snapshot without hitting the network.
    /// Returns <c>null</c> when no check has run yet (cache file
    /// absent or unreadable).
    /// </summary>
    public static PluginUpdateCheckSnapshot? ReadCached()
    {
        if (!File.Exists(CachePath)) return null;
        try
        {
            var json = File.ReadAllText(CachePath);
            return JsonSerializer.Deserialize<PluginUpdateCheckSnapshot>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static List<(string PackageId, string InstalledVersion)> ListInstalledSiblings()
    {
        var list = new List<(string, string)>();
        if (!Directory.Exists(PluginDir)) return list;

        foreach (var dir in Directory.GetDirectories(PluginDir))
        {
            var metaPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(metaPath)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (!doc.RootElement.TryGetProperty("packageId", out var idEl)) continue;
                if (!doc.RootElement.TryGetProperty("version", out var verEl)) continue;
                var id = idEl.GetString();
                var ver = verEl.GetString();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(ver))
                {
                    list.Add((id!, ver!));
                }
            }
            catch { /* skip broken plugin.json */ }
        }
        return list;
    }

    private async Task<string?> FetchLatestAsync(
        string packageId, bool includePrerelease, CancellationToken ct)
    {
        // NuGet's v3-flatcontainer requires lowercase package ids; the
        // CA1308 'use ToUpperInvariant' guidance doesn't apply to URL
        // path segments.
#pragma warning disable CA1308
        var idLower = packageId.ToLowerInvariant();
#pragma warning restore CA1308
        var url = $"https://api.nuget.org/v3-flatcontainer/{idLower}/index.json";

        using var resp = await _http.GetAsync(new Uri(url), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var versions = doc.RootElement.GetProperty("versions").EnumerateArray()
            .Select(v => v.GetString())
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();

        return includePrerelease
            ? versions.LastOrDefault()
            : versions.LastOrDefault(v => v is not null && !v.Contains('-', StringComparison.Ordinal));
    }

    private static async Task WriteCacheAsync(
        PluginUpdateCheckSnapshot snapshot, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(CachePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(snapshot, JsonOpts);
        await File.WriteAllTextAsync(CachePath, json, ct).ConfigureAwait(false);
    }
}

/// <summary>One row in the update-check snapshot.</summary>
public sealed record PluginUpdateCheckResult(
    string PackageId,
    string Installed,
    string? Latest,
    bool UpdateAvailable,
    string? Error);

/// <summary>Persisted shape of an update-check run.</summary>
public sealed record PluginUpdateCheckSnapshot(
    DateTimeOffset CheckedAt,
    bool IncludePrerelease,
    IReadOnlyList<PluginUpdateCheckResult> Results);
