// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Sources;

/// <summary>
/// Wire shape for a UI-driven catalogue-provider override (#309).
/// Sent by the Settings → Catalogue providers UI to
/// <c>POST /api/catalogue/config</c>. Persists to
/// <c>~/.bowire/catalogue-config.json</c> so the override survives a
/// process restart; absent file ⇒ <c>appsettings.json</c> fallback.
/// </summary>
/// <remarks>
/// The override is process-wide. Per-process at-most-one-provider is
/// the architectural intent of <see cref="IBowireCatalogueProvider"/>
/// — UI persistence is per-workspace on the client (so an operator
/// who switches workspaces re-applies the right config) but the
/// applied state on the host is shared.
/// </remarks>
public sealed class BowireCatalogueOverride
{
    /// <summary>Provider id (e.g. <c>"local"</c>, <c>"http"</c>, <c>"consul"</c>).
    /// Null / empty clears the override and falls back to appsettings.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    /// <summary>Inline options snapshot for the selected provider —
    /// only the fields relevant to <see cref="Provider"/> are read.</summary>
    [JsonPropertyName("local")]
    public BowireLocalCatalogueOptions? Local { get; set; }

    /// <summary>HTTP-provider options (URL + Authorization header).</summary>
    [JsonPropertyName("http")]
    public BowireHttpCatalogueOptions? Http { get; set; }

    /// <summary>Consul-provider options (address + token + DC + tag).</summary>
    [JsonPropertyName("consul")]
    public BowireConsulCatalogueOptions? Consul { get; set; }
}

/// <summary>
/// File-backed override store. Reads / writes
/// <c>~/.bowire/catalogue-config.json</c> and hydrates the
/// <see cref="BowireCatalogueProviderAccessor"/> on first construction.
/// </summary>
public sealed class BowireCatalogueOverrideStore
{
    private readonly BowireCatalogueProviderAccessor _accessor;
    private readonly ILogger? _logger;
    private readonly object _writeLock = new();
    private BowireCatalogueOverride? _current;

    /// <summary>Construct the store + re-hydrate any persisted
    /// override.</summary>
    public BowireCatalogueOverrideStore(
        BowireCatalogueProviderAccessor accessor,
        ILoggerFactory? loggerFactory = null)
    {
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
        _logger = loggerFactory?.CreateLogger("Kuestenlogik.Bowire.Catalogue.Override");
        _current = Load();
        if (_current is not null) ApplyOverride(_current);
    }

    /// <summary>Snapshot of the persisted override (or <c>null</c>
    /// when none).</summary>
    public BowireCatalogueOverride? Current
    {
        get { lock (_writeLock) return _current; }
    }

    /// <summary>Persist + apply a new override.</summary>
    public void Save(BowireCatalogueOverride payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        lock (_writeLock)
        {
            _current = payload;
            try { File.WriteAllText(ResolvePath(), JsonSerializer.Serialize(payload, JsonOptions)); }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (_logger is not null) PersistFailed(_logger, ex);
            }
            ApplyOverride(payload);
        }
    }

    /// <summary>Clear the override + restore the appsettings fallback.</summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            _current = null;
            try
            {
                var path = ResolvePath();
                if (File.Exists(path)) File.Delete(path);
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (_logger is not null) PersistFailed(_logger, ex);
            }
            _accessor.SetOverride(null);
        }
    }

    private void ApplyOverride(BowireCatalogueOverride payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Provider))
        {
            _accessor.SetOverride(null);
            return;
        }
        var provider = BuildProvider(payload);
        _accessor.SetOverride(provider);
    }

    private static IBowireCatalogueProvider? BuildProvider(BowireCatalogueOverride payload)
    {
        // Compare case-insensitively without round-tripping through
        // ToLowerInvariant (CA1308) — the wire shape is documented as
        // lower-case but we accept any casing the operator types.
        var providerId = payload.Provider ?? string.Empty;
        if (string.Equals(providerId, "local", StringComparison.OrdinalIgnoreCase))
        {
            var opts = payload.Local ?? new BowireLocalCatalogueOptions();
            return new LocalCatalogueProvider(() =>
                string.IsNullOrWhiteSpace(opts.Path)
                    ? LocalCatalogueProvider.ResolveDefaultPath()
                    : opts.Path!);
        }
        if (string.Equals(providerId, "http", StringComparison.OrdinalIgnoreCase))
        {
            var opts = payload.Http ?? new BowireHttpCatalogueOptions();
            return new HttpCatalogueProvider(() => opts, () => new HttpClient());
        }
        if (string.Equals(providerId, "consul", StringComparison.OrdinalIgnoreCase))
        {
            var opts = payload.Consul ?? new BowireConsulCatalogueOptions();
            return new ConsulCatalogueProvider(() => opts, () => new HttpClient());
        }
        return null;
    }

    private static BowireCatalogueOverride? Load()
    {
        try
        {
            var path = ResolvePath();
            if (!File.Exists(path)) return null;
            var raw = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return JsonSerializer.Deserialize<BowireCatalogueOverride>(raw, JsonOptions);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch
#pragma warning restore CA1031
        {
            return null;
        }
    }

    internal static string ResolvePath()
    {
        var envOverride = Environment.GetEnvironmentVariable("BOWIRE_CATALOGUE_CONFIG_PATH");
        if (!string.IsNullOrEmpty(envOverride)) return envOverride;
        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.None);
        if (string.IsNullOrEmpty(home)) return string.Empty;
        var dir = Path.Combine(home, ".bowire");
        try { Directory.CreateDirectory(dir); }
#pragma warning disable CA1031
        catch { /* best-effort */ }
#pragma warning restore CA1031
        return Path.Combine(dir, "catalogue-config.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Action<ILogger, Exception> PersistFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1, nameof(PersistFailed)),
            "Failed to persist catalogue-config override to disk.");
}
