// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Sources;

/// <summary>
/// DI helpers for wiring an <see cref="IBowireCatalogueProvider"/>
/// into the host (#136). Pair with <c>AddBowire()</c>:
/// </summary>
/// <example>
/// <code>
/// builder.Services
///        .AddBowire()
///        .AddBowireCatalogue(builder.Configuration);
/// </code>
/// </example>
public static class BowireCatalogueServiceCollectionExtensions
{
    /// <summary>
    /// Wire the catalogue-provider seam. Binds
    /// <c>Bowire:Discovery:Catalogue</c>,
    /// <c>Bowire:Discovery:Catalogue:Local</c>,
    /// <c>Bowire:Discovery:Catalogue:Http</c>, and
    /// <c>Bowire:Discovery:Catalogue:Consul</c> via the standard
    /// options pattern, and registers the configured provider via
    /// the <see cref="BowireCatalogueProviderAccessor"/> singleton.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Calling this is a no-op when
    /// <c>Bowire:Discovery:Catalogue:Provider</c> is unset — the
    /// accessor resolves to a null provider and the catalogue
    /// endpoint short-circuits to an empty list. Setting it to an
    /// unknown id throws at first accessor resolution so a typo in
    /// <c>appsettings.json</c> surfaces immediately.
    /// </para>
    /// <para>
    /// The endpoint <c>GET /api/catalogue/entries</c> is mapped
    /// unconditionally by <c>MapBowire()</c> — it just returns an
    /// empty list when no provider is registered.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddBowireCatalogue(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<BowireCatalogueOptions>()
                .Bind(configuration.GetSection("Bowire:Discovery:Catalogue"));
        services.AddOptions<BowireLocalCatalogueOptions>()
                .Bind(configuration.GetSection("Bowire:Discovery:Catalogue:Local"));
        services.AddOptions<BowireHttpCatalogueOptions>()
                .Bind(configuration.GetSection("Bowire:Discovery:Catalogue:Http"));
        services.AddOptions<BowireConsulCatalogueOptions>()
                .Bind(configuration.GetSection("Bowire:Discovery:Catalogue:Consul"));

        services.TryAddSingleton(sp => BuildAccessor(sp));
        return services;
    }

    /// <summary>
    /// Overload that takes an explicit configure callback for hosts
    /// that don't bind <c>IConfiguration</c>. Mirrors the
    /// <c>AddBowire(Action&lt;BowireOptions&gt;)</c> shape.
    /// </summary>
    public static IServiceCollection AddBowireCatalogue(
        this IServiceCollection services,
        Action<BowireCatalogueOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<BowireCatalogueOptions>().Configure(configure);
        services.AddOptions<BowireLocalCatalogueOptions>();
        services.AddOptions<BowireHttpCatalogueOptions>();
        services.AddOptions<BowireConsulCatalogueOptions>();

        services.TryAddSingleton(sp => BuildAccessor(sp));
        return services;
    }

    private static BowireCatalogueProviderAccessor BuildAccessor(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<BowireCatalogueOptions>>().Value;
        var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("Kuestenlogik.Bowire.Catalogue");
        var provider = BowireCatalogueProviderRegistry.Resolve(options, logger);
        if (provider is null) return new BowireCatalogueProviderAccessor(null);

        // Replace the assembly-scan-instantiated singleton with one
        // that has its options snapshot wired so per-fetch resolution
        // sees IConfiguration-bound values. For the three built-in
        // providers the parameterless ctor is fine (it uses env-var
        // defaults); the explicit options-aware ctor takes precedence.
        var bound = provider switch
        {
            LocalCatalogueProvider => (IBowireCatalogueProvider)new LocalCatalogueProvider(() =>
            {
                var localOptions = sp.GetRequiredService<IOptions<BowireLocalCatalogueOptions>>().Value;
                return string.IsNullOrWhiteSpace(localOptions.Path)
                    ? LocalCatalogueProvider.ResolveDefaultPath()
                    : localOptions.Path!;
            }),
            HttpCatalogueProvider => new HttpCatalogueProvider(
                () => sp.GetRequiredService<IOptions<BowireHttpCatalogueOptions>>().Value,
                () => new System.Net.Http.HttpClient()),
            ConsulCatalogueProvider => new ConsulCatalogueProvider(
                () => sp.GetRequiredService<IOptions<BowireConsulCatalogueOptions>>().Value,
                () => new System.Net.Http.HttpClient()),
            _ => provider, // 3rd-party providers manage their own wiring.
        };
        return new BowireCatalogueProviderAccessor(bound);
    }
}

/// <summary>
/// Singleton wrapper around the resolved
/// <see cref="IBowireCatalogueProvider"/> (or <c>null</c> when the
/// host hasn't selected one). Lets DI hold a non-null reference even
/// when no provider is configured — the endpoint reads
/// <see cref="Provider"/> and treats null as "empty catalogue".
/// </summary>
public sealed class BowireCatalogueProviderAccessor
{
    /// <summary>
    /// Construct an accessor wrapping the resolved provider (or
    /// <c>null</c> when no provider is configured).
    /// </summary>
    /// <param name="provider">The active provider, or <c>null</c>.</param>
    public BowireCatalogueProviderAccessor(IBowireCatalogueProvider? provider)
    {
        Provider = provider;
    }

    /// <summary>
    /// The active catalogue provider, or <c>null</c> when no
    /// provider is configured. Consumers should treat <c>null</c> as
    /// "no catalogue" — symmetric with an empty fetch result.
    /// </summary>
    public IBowireCatalogueProvider? Provider { get; }
}
