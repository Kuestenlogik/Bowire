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
    private const string Filename = "ai-config.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Load the persisted config and return a <see cref="BowireAiOptions"/>
    /// populated from it. Returns <c>null</c> when no file is present so
    /// callers can distinguish "user hasn't picked anything" from "user
    /// explicitly saved these defaults".
    /// </summary>
    public static BowireAiOptions? TryLoad()
    {
        try
        {
            var path = BowireUserContext.GetUserPath(Filename);
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
    /// Persist <paramref name="opts"/> to <c>ai-config.json</c>. Creates
    /// the parent directory if needed. Throws on I/O errors so the
    /// caller (the <c>POST /api/ai/config</c> handler) can return 500
    /// with the underlying message instead of silently dropping the
    /// user's pick.
    /// </summary>
    public static void Save(BowireAiOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var path = BowireUserContext.GetUserPath(Filename);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var dto = PersistedConfig.From(opts);
        var json = JsonSerializer.Serialize(dto, JsonOpts);
        File.WriteAllText(path, json);
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
