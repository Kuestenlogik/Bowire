// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Catalogue.Kubernetes;

/// <summary>
/// Wires the Kubernetes catalogue provider so it picks up its
/// IConfiguration-bound options instead of running with the
/// parameterless-ctor defaults (#305 Phase D).
/// </summary>
/// <remarks>
/// <para>
/// Order matters: call <see cref="AddBowireKubernetesCatalogue(IServiceCollection,IConfiguration)"/>
/// BEFORE <c>AddBowireCatalogue</c> so the
/// <see cref="BowireKubernetesCatalogueOptions"/> binding is in
/// place when the accessor singleton is built — same convention as
/// the core http / consul providers.
/// </para>
/// <para>
/// The core <c>BuildAccessor</c> falls through 3rd-party providers to
/// the parameterless-ctor instance; this extension replaces the
/// accessor's provider with an options-aware
/// <see cref="KubernetesCatalogueProvider"/> when
/// <c>Bowire:Discovery:Catalogue:Provider == "kubernetes"</c>.
/// </para>
/// </remarks>
public static class BowireKubernetesCatalogueServiceCollectionExtensions
{
    /// <summary>
    /// Bind <c>Bowire:Discovery:Catalogue:Kubernetes</c> and replace
    /// the parameterless-ctor provider in the
    /// <see cref="BowireCatalogueProviderAccessor"/> with one that
    /// reads options from configuration on every fetch.
    /// </summary>
    public static IServiceCollection AddBowireKubernetesCatalogue(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<BowireKubernetesCatalogueOptions>()
                .Bind(configuration.GetSection("Bowire:Discovery:Catalogue:Kubernetes"));
        RegisterAccessorOverride(services);
        return services;
    }

    /// <summary>
    /// Overload for hosts that don't bind <c>IConfiguration</c>.
    /// </summary>
    public static IServiceCollection AddBowireKubernetesCatalogue(
        this IServiceCollection services,
        Action<BowireKubernetesCatalogueOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<BowireKubernetesCatalogueOptions>().Configure(configure);
        RegisterAccessorOverride(services);
        return services;
    }

    private static void RegisterAccessorOverride(IServiceCollection services)
    {
        // Replace whatever accessor BuildAccessor produced earlier
        // (or wire one ourselves if AddBowireCatalogue runs after
        // us). Either way the resolved provider is the options-aware
        // KubernetesCatalogueProvider — the parameterless instance
        // assembly-scanned through the registry is discarded.
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<BowireCatalogueOptions>>().Value;
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("Kuestenlogik.Bowire.Catalogue.Kubernetes");
            if (!string.Equals(options.Provider, "kubernetes", StringComparison.OrdinalIgnoreCase))
            {
                // Caller selected a different provider — fall back to
                // the registry's choice so we don't shadow it.
                var fallback = BowireCatalogueProviderRegistry.Resolve(options, logger);
                return new BowireCatalogueProviderAccessor(fallback);
            }
            var provider = new KubernetesCatalogueProvider(
                () => sp.GetRequiredService<IOptions<BowireKubernetesCatalogueOptions>>().Value,
                handler => new System.Net.Http.HttpClient(handler, disposeHandler: false),
                new DefaultKubernetesEnvironment());
            return new BowireCatalogueProviderAccessor(provider);
        });
    }
}
