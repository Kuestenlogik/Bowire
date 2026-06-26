// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Sources;

/// <summary>
/// Reads catalogue entries from a JSON file on disk — the simplest
/// of the built-in providers (#136 Phase A). The path comes from
/// <see cref="BowireLocalCatalogueOptions.Path"/>; when null the
/// provider falls back to <c>~/.bowire/catalogue.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// A missing file resolves to an empty catalogue rather than an
/// error so a host can enable the provider before any catalogue
/// file exists and have the first write light up automatically on
/// the next refresh. Malformed JSON DOES throw — the registry
/// surfaces it on <c>GET /api/catalogue/entries</c> so the operator
/// can spot the typo without spelunking through logs.
/// </para>
/// <para>
/// The reader runs on a fresh <see cref="FileStream"/> per refresh
/// — no in-memory cache — because the whole point of the local
/// provider is editing the file by hand and re-loading. The cost is
/// trivial for any realistic catalogue size; if a multi-tenant
/// deployment ever needs a faster read path it can swap in the
/// <c>http</c> provider pointing at a relay.
/// </para>
/// </remarks>
public sealed class LocalCatalogueProvider : IBowireCatalogueProvider
{
    private readonly Func<string> _pathResolver;

    /// <summary>
    /// Parameterless ctor for the assembly-scan discovery path.
    /// Uses the default path resolution (env <c>BOWIRE_CATALOGUE_PATH</c>
    /// override → <c>~/.bowire/catalogue.json</c>) so the registry
    /// can instantiate the provider without any host-side wiring.
    /// </summary>
    public LocalCatalogueProvider() : this(() => ResolveDefaultPath()) { }

    /// <summary>
    /// Test seam — pass an explicit path resolver. Production code
    /// uses the parameterless ctor; tests pass a constant path so
    /// they don't depend on the user-profile layout.
    /// </summary>
    internal LocalCatalogueProvider(Func<string> pathResolver)
    {
        _pathResolver = pathResolver;
    }

    /// <inheritdoc/>
    public string Id => "local";

    /// <inheritdoc/>
    public string Name => "Local file";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BowireCatalogueEntry>> FetchAsync(CancellationToken cancellationToken)
    {
        var path = _pathResolver();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return Array.Empty<BowireCatalogueEntry>();
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        var doc = await JsonSerializer.DeserializeAsync<BowireCatalogueDocument>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        if (doc?.Entries is null) return Array.Empty<BowireCatalogueEntry>();

        // Drop rows with no URL — required field per the spec, but a
        // hand-edited file could land here with a stray empty entry.
        // Filtering here keeps every downstream consumer free of the
        // null check.
        var filtered = new List<BowireCatalogueEntry>(doc.Entries.Count);
        foreach (var entry in doc.Entries)
        {
            if (entry is null) continue;
            if (string.IsNullOrWhiteSpace(entry.Url)) continue;
            filtered.Add(entry);
        }
        return filtered;
    }

    /// <summary>
    /// Resolve the default path for <c>~/.bowire/catalogue.json</c>.
    /// The <c>BOWIRE_CATALOGUE_PATH</c> environment variable acts as a
    /// per-process override — handy for tests and for hosts that
    /// want to point at a custom location without binding options
    /// explicitly.
    /// </summary>
    internal static string ResolveDefaultPath()
    {
        var envOverride = Environment.GetEnvironmentVariable("BOWIRE_CATALOGUE_PATH");
        if (!string.IsNullOrEmpty(envOverride)) return envOverride;

        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.None);
        if (string.IsNullOrEmpty(home)) return string.Empty;

        return Path.Combine(home, ".bowire", "catalogue.json");
    }

    /// <summary>
    /// Shared JSON serializer settings — case-insensitive property
    /// matching so a hand-edited <c>"URL"</c> still resolves, and
    /// trailing commas + comments tolerated for the same reason.
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}
