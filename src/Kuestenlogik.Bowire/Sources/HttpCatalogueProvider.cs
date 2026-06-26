// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Net.Http.Json;

namespace Kuestenlogik.Bowire.Sources;

/// <summary>
/// Fetches a <see cref="BowireCatalogueDocument"/> from a configured
/// URL (#136 Phase B). Bound to
/// <c>Bowire:Discovery:Catalogue:Http</c> via
/// <see cref="BowireHttpCatalogueOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// The provider expects the URL to return the document shape
/// documented on <see cref="BowireCatalogueDocument"/>:
/// </para>
/// <code>
/// { "version": 1, "entries": [ { "url": "https://..." }, ... ] }
/// </code>
/// <para>
/// A missing / empty URL resolves to an empty list (no throw) so a
/// half-configured host doesn't crash at startup. Non-2xx
/// responses, network errors, and malformed JSON propagate as
/// exceptions — the catalogue endpoint surfaces them as
/// problem-details so the operator can spot the misconfiguration.
/// </para>
/// <para>
/// The provider takes an explicit options snapshot at fetch time,
/// re-resolved on every refresh, so config edits land without a
/// restart. Same pattern the Bowire OAuth proxy uses.
/// </para>
/// </remarks>
public sealed class HttpCatalogueProvider : IBowireCatalogueProvider
{
    private readonly Func<BowireHttpCatalogueOptions> _optionsResolver;
    private readonly Func<HttpClient> _clientFactory;

    /// <summary>
    /// Parameterless ctor for the assembly-scan discovery path.
    /// Uses environment-variable-driven defaults so the registry
    /// can instantiate the provider before <c>AddBowireCatalogue</c>
    /// has wired explicit options.
    /// </summary>
    public HttpCatalogueProvider() : this(
        () => ResolveDefaultOptions(),
        () => new HttpClient())
    { }

    /// <summary>
    /// Test seam — pass an explicit options resolver and HTTP-client
    /// factory. Production code uses the parameterless ctor; tests
    /// inject a <see cref="HttpClient"/> wrapping a mock handler so
    /// they don't make real network calls.
    /// </summary>
    internal HttpCatalogueProvider(
        Func<BowireHttpCatalogueOptions> optionsResolver,
        Func<HttpClient> clientFactory)
    {
        _optionsResolver = optionsResolver;
        _clientFactory = clientFactory;
    }

    /// <inheritdoc/>
    public string Id => "http";

    /// <inheritdoc/>
    public string Name => "HTTP endpoint";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BowireCatalogueEntry>> FetchAsync(CancellationToken cancellationToken)
    {
        var options = _optionsResolver();
        if (string.IsNullOrWhiteSpace(options.Url))
        {
            return Array.Empty<BowireCatalogueEntry>();
        }

        using var client = _clientFactory();
        client.Timeout = options.Timeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(10)
            : options.Timeout;

        using var request = new HttpRequestMessage(HttpMethod.Get, options.Url);
        if (!string.IsNullOrWhiteSpace(options.Authorization))
        {
            // Assign as a raw header so the operator can pick any scheme
            // (Bearer, Basic, ApiKey, ...) without us second-guessing it.
            request.Headers.TryAddWithoutValidation("Authorization", options.Authorization);
        }
        // Accept JSON explicitly so a content-negotiating server picks
        // the right shape when multiple representations are available.
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var doc = await response.Content.ReadFromJsonAsync<BowireCatalogueDocument>(
            LocalCatalogueProvider.JsonOptions,
            cancellationToken).ConfigureAwait(false);

        if (doc?.Entries is null) return Array.Empty<BowireCatalogueEntry>();

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
    /// Resolve the default options from environment variables. The
    /// catalogue URL falls out of <c>BOWIRE_CATALOGUE_HTTP_URL</c>;
    /// the auth header out of
    /// <c>BOWIRE_CATALOGUE_HTTP_AUTHORIZATION</c>. Both are optional
    /// — when neither is set the provider returns an empty list.
    /// </summary>
    /// <remarks>
    /// In production hosts these get re-bound via
    /// <c>AddBowireCatalogue(IConfiguration)</c> before the first
    /// fetch; the env-var fallback exists so the provider stays
    /// usable in tests and one-off CLI invocations where wiring full
    /// options is overkill.
    /// </remarks>
    internal static BowireHttpCatalogueOptions ResolveDefaultOptions()
    {
        return new BowireHttpCatalogueOptions
        {
            Url = Environment.GetEnvironmentVariable("BOWIRE_CATALOGUE_HTTP_URL"),
            Authorization = Environment.GetEnvironmentVariable("BOWIRE_CATALOGUE_HTTP_AUTHORIZATION"),
        };
    }
}
