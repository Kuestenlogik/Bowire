// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Sources;

namespace Kuestenlogik.Bowire.Catalogue.Agent;

/// <summary>
/// Sources Bowire's URL/service catalogue from a Bowire Agent hub
/// (#305 Phase E). It reads the hub's <c>GET {HubUrl}/hub/agents/catalogue</c>
/// (#128, <c>MapBowireHub</c>) and returns one
/// <see cref="BowireCatalogueEntry"/> per entry of every live agent;
/// without a hub URL it returns an empty list and keeps the workbench's
/// "no catalogue" surface intact.
/// </summary>
/// <remarks>
/// <para>
/// The wire shape (see <see cref="HubCatalogueDocument"/>) is the
/// contract the hub publishes — locked here so an installation with
/// its own aggregator can validate it against
/// <see cref="BowireAgentCatalogueOptions.StubResponse"/>. The provider's behaviour is intentionally simple:
/// merge each agent's entries into the catalogue, prefix every tag
/// with the agent's id so operators can filter to a single agent in
/// the workbench, and surface the agent's service name as the entry
/// label when the entry doesn't carry its own.
/// </para>
/// <para>
/// Agents push their registration to the hub; the workbench polls the
/// hub's catalogue with its existing refresh-on-interval loop, exactly
/// like the http / consul providers. A push-notify path from hub to
/// workbench is not part of it.
/// </para>
/// </remarks>
public sealed class AgentCatalogueProvider : IBowireCatalogueProvider
{
    private readonly Func<BowireAgentCatalogueOptions> _optionsResolver;
    private readonly Func<HttpClient> _clientFactory;

    /// <summary>
    /// Parameterless ctor for the assembly-scan discovery path.
    /// Uses the no-options defaults; the provider returns an empty
    /// list when neither <see cref="BowireAgentCatalogueOptions.HubUrl"/>
    /// nor <see cref="BowireAgentCatalogueOptions.StubResponse"/> is
    /// configured.
    /// </summary>
    public AgentCatalogueProvider() : this(
        () => new BowireAgentCatalogueOptions(),
        () => new HttpClient())
    { }

    /// <summary>
    /// Test seam — pass an explicit options resolver and HTTP-client
    /// factory.
    /// </summary>
    internal AgentCatalogueProvider(
        Func<BowireAgentCatalogueOptions> optionsResolver,
        Func<HttpClient> clientFactory)
    {
        _optionsResolver = optionsResolver;
        _clientFactory = clientFactory;
    }

    /// <inheritdoc/>
    public string Id => "agent";

    /// <inheritdoc/>
    public string Name => "Bowire Agent hub";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BowireCatalogueEntry>> FetchAsync(CancellationToken cancellationToken)
    {
        var options = _optionsResolver();

        // Stub path: parse the configured JSON snapshot instead of
        // hitting the wire. Lets installations validate their own
        // aggregator payload against the wire-shape contract.
        if (!string.IsNullOrWhiteSpace(options.StubResponse))
        {
            return ParseDocument(options.StubResponse!);
        }

        if (string.IsNullOrWhiteSpace(options.HubUrl))
        {
            // Hub not configured: an empty catalogue, same as the
            // unconfigured-provider path.
            return Array.Empty<BowireCatalogueEntry>();
        }

        using var client = _clientFactory();
        client.Timeout = options.Timeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(10)
            : options.Timeout;

        var catalogueUrl = $"{options.HubUrl!.TrimEnd('/')}/hub/agents/catalogue";
        using var request = new HttpRequestMessage(HttpMethod.Get, catalogueUrl);
        if (!string.IsNullOrWhiteSpace(options.BootstrapToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {options.BootstrapToken}");
        }
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var doc = await response.Content.ReadFromJsonAsync<HubCatalogueDocument>(
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        return Materialise(doc);
    }

    /// <summary>
    /// Parse a hub-catalogue JSON string and materialise its
    /// entries. Exposed for the stub-response path + tests.
    /// </summary>
    internal static IReadOnlyList<BowireCatalogueEntry> ParseDocument(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var doc = JsonSerializer.Deserialize<HubCatalogueDocument>(
            bytes,
            JsonOptions);
        return Materialise(doc);
    }

    private static IReadOnlyList<BowireCatalogueEntry> Materialise(HubCatalogueDocument? doc)
    {
        if (doc?.Agents is null || doc.Agents.Count == 0)
        {
            return Array.Empty<BowireCatalogueEntry>();
        }

        var entries = new List<BowireCatalogueEntry>();
        foreach (var agent in doc.Agents)
        {
            if (agent is null) continue;
            if (agent.Entries is null || agent.Entries.Count == 0) continue;

            foreach (var entry in agent.Entries)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Url)) continue;

                // Tag composition: agent-id + service-name + the
                // agent's own tags + the entry's own tags — every
                // axis the workbench's filter popup might key on.
                var tags = new List<string>();
                if (!string.IsNullOrWhiteSpace(agent.AgentId))
                    tags.Add($"agent:{agent.AgentId}");
                if (!string.IsNullOrWhiteSpace(agent.ServiceName))
                    tags.Add($"service:{agent.ServiceName}");
                if (agent.Tags is not null) tags.AddRange(agent.Tags);
                if (entry.Tags is not null) tags.AddRange(entry.Tags);

                entries.Add(new BowireCatalogueEntry(
                    Url: entry.Url,
                    Name: entry.Name ?? agent.ServiceName ?? agent.AgentId,
                    Protocols: entry.Protocols,
                    Tags: tags.Count == 0 ? null : tags,
                    Schema: entry.Schema));
            }
        }
        return entries;
    }

    /// <summary>
    /// JSON options shared with the rest of the provider. Same shape
    /// as the other catalogue providers — case-insensitive +
    /// tolerant of trailing commas / comments — but locally owned so
    /// the sibling assembly doesn't need a private reach into core.
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // === Wire shape — what MapBowireHub serves (#128), locked here
    //     so installations can validate their own aggregator against it. ===

    /// <summary>
    /// Top-level shape returned by the hub's
    /// <c>GET /hub/agents/catalogue</c> aggregator (#128).
    /// </summary>
    internal sealed record HubCatalogueDocument(
        [property: JsonPropertyName("version")] int Version = 1,
        [property: JsonPropertyName("agents")] IReadOnlyList<HubAgent>? Agents = null);

    /// <summary>
    /// One registered agent's contribution to the hub catalogue.
    /// </summary>
    internal sealed record HubAgent(
        [property: JsonPropertyName("agentId")] string? AgentId,
        [property: JsonPropertyName("serviceName")] string? ServiceName,
        [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
        [property: JsonPropertyName("entries")] IReadOnlyList<BowireCatalogueEntry>? Entries);
}
