// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Catalogue.Agent;

/// <summary>
/// Wires the Bowire-Agent catalogue provider so it picks up its
/// IConfiguration-bound options (#305 Phase E).
/// </summary>
public static class BowireAgentCatalogueServiceCollectionExtensions
{
    /// <summary>
    /// Bind <c>Bowire:Discovery:Catalogue:Agent</c> and replace the
    /// parameterless-ctor provider in the
    /// <see cref="BowireCatalogueProviderAccessor"/> with an
    /// options-aware <see cref="AgentCatalogueProvider"/>. Same shape
    /// as the Kubernetes sibling extension.
    /// </summary>
    public static IServiceCollection AddBowireAgentCatalogue(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<BowireAgentCatalogueOptions>()
                .Bind(configuration.GetSection("Bowire:Discovery:Catalogue:Agent"));
        RegisterAccessorOverride(services);
        return services;
    }

    /// <summary>Overload for hosts that don't bind <c>IConfiguration</c>.</summary>
    public static IServiceCollection AddBowireAgentCatalogue(
        this IServiceCollection services,
        Action<BowireAgentCatalogueOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<BowireAgentCatalogueOptions>().Configure(configure);
        RegisterAccessorOverride(services);
        return services;
    }

    private static void RegisterAccessorOverride(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<BowireCatalogueOptions>>().Value;
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("Kuestenlogik.Bowire.Catalogue.Agent");
            if (!string.Equals(options.Provider, "agent", StringComparison.OrdinalIgnoreCase))
            {
                var fallback = BowireCatalogueProviderRegistry.Resolve(options, logger);
                return new BowireCatalogueProviderAccessor(fallback);
            }
            var provider = new AgentCatalogueProvider(
                () => sp.GetRequiredService<IOptions<BowireAgentCatalogueOptions>>().Value,
                () => new System.Net.Http.HttpClient());
            return new BowireCatalogueProviderAccessor(provider);
        });
    }
}
