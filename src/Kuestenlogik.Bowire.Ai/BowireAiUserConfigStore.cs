// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Ai;

/// <summary>
/// Reads + writes the per-user AI configuration (<c>ai-config.json</c>)
/// through the <see cref="IBowireUserStore"/> seam (#28 Phase 2) so the
/// Settings UI (#63) survives restart. Single-user installs land at
/// <c>~/.bowire/ai-config.json</c>; multi-tenant installs partition by
/// authenticated identity once the SCIM phase ships.
/// </summary>
/// <remarks>
/// The store layers on top of <see cref="BowireAiOptions"/> defaults but
/// never collapses the file to "everything matches the default" — that
/// would race the overlay precedence (disk wins over env vars wins over
/// CLI flags wins over <see cref="BowireAiOptions"/> defaults). When the
/// user picks the same value as the default we still persist it so the
/// next restart doesn't surprise them with a downstream config change.
/// </remarks>
public static class BowireAiUserConfigStore
{
    private const string GlobalFilename = "ai-config.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // #116 Phase 3 — per-workspace override path. A workspace's
    // override is stored as ai-config.<workspaceId>.json next to
    // the global ai-config.json. Resolution at load time prefers
    // the override; falls back to global; falls back to runtime
    // defaults. Operators who never opt a workspace into its own
    // config see no behavioural change.
    private static string OverrideFilename(string workspaceId)
        => $"ai-config.{SanitiseWorkspaceId(workspaceId)}.json";

    private static string SanitiseWorkspaceId(string id)
    {
        // Workspace IDs are generated client-side as 'ws_' + a-z0-9
        // slug, plus the seeded 'personal'. Defensive sanitisation
        // here keeps a hostile workspaceId out of the file path.
        var sb = new System.Text.StringBuilder(id.Length);
        foreach (var c in id)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
        }
        var sanitised = sb.ToString();
        return sanitised.Length == 0 ? "default" : sanitised;
    }

    /// <summary>
    /// Load the persisted config and return a <see cref="BowireAiOptions"/>
    /// populated from it. Returns <c>null</c> when no file is present so
    /// callers can distinguish "user hasn't picked anything" from "user
    /// explicitly saved these defaults".
    /// </summary>
    /// <param name="workspaceId">
    /// When set, prefer the workspace's own override file. Falls back to
    /// global when the override doesn't exist.
    /// </param>
    public static BowireAiOptions? TryLoad(string? workspaceId = null)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            var overrideOpts = TryLoadFile(OverrideFilename(workspaceId));
            if (overrideOpts is not null) return overrideOpts;
        }
        return TryLoadFile(GlobalFilename);
    }

    private static BowireAiOptions? TryLoadFile(string filename)
    {
        try
        {
            var path = BowireUserContext.GetUserPath(filename);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<PersistedConfig>(json, JsonOpts);
            return dto?.ToOptions();
        }
        catch
        {
            // A corrupted user-config file shouldn't take the workbench
            // down on startup. Skip + fall back to the IConfiguration
            // overlay; the next save rewrites the file with valid JSON.
            return null;
        }
    }

    /// <summary>
    /// Persist <paramref name="opts"/> to <c>ai-config.json</c> when no
    /// workspaceId is given, or to <c>ai-config.&lt;workspaceId&gt;.json</c>
    /// when one is. Creates the parent directory if needed. Throws on
    /// I/O errors so the caller (the <c>POST /api/ai/config</c> handler)
    /// can return 500 with the underlying message instead of silently
    /// dropping the user's pick.
    /// </summary>
    public static void Save(BowireAiOptions opts, string? workspaceId = null)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var filename = string.IsNullOrWhiteSpace(workspaceId)
            ? GlobalFilename
            : OverrideFilename(workspaceId!);
        var path = BowireUserContext.GetUserPath(filename);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var dto = PersistedConfig.From(opts);
        var json = JsonSerializer.Serialize(dto, JsonOpts);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Drop a workspace's per-workspace override so loads fall back
    /// to the global config. No-op when no override exists.
    /// </summary>
    public static void RemoveOverride(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return;
        try
        {
            var path = BowireUserContext.GetUserPath(OverrideFilename(workspaceId));
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup; the override stays on disk and
            // continues to apply. Not worth surfacing as a 500.
        }
    }

    /// <summary>
    /// True when a per-workspace override file exists for the given
    /// workspace id. Used by the API to flag "this workspace is
    /// overriding the global config" so the UI can render correctly.
    /// </summary>
    public static bool HasOverride(string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return false;
        try
        {
            return File.Exists(BowireUserContext.GetUserPath(OverrideFilename(workspaceId!)));
        }
        catch { return false; }
    }

    // Persisted shape stays decoupled from BowireAiOptions so renaming a
    // property doesn't break existing on-disk files. New fields default to
    // null and the loader falls back to the runtime default.
    private sealed class PersistedConfig
    {
        public string? ProviderId { get; set; }
        public string? Endpoint { get; set; }
        public string? Model { get; set; }
        public bool? AutoDetectLocal { get; set; }

        public static PersistedConfig From(BowireAiOptions opts) => new()
        {
            ProviderId = opts.ProviderId,
            Endpoint = opts.Endpoint,
            Model = opts.Model,
            AutoDetectLocal = opts.AutoDetectLocal,
        };

        public BowireAiOptions ToOptions()
        {
            var defaults = new BowireAiOptions();
            return new BowireAiOptions
            {
                ProviderId = ProviderId ?? defaults.ProviderId,
                Endpoint = Endpoint ?? defaults.Endpoint,
                Model = Model ?? defaults.Model,
                AutoDetectLocal = AutoDetectLocal ?? defaults.AutoDetectLocal,
            };
        }
    }
}
