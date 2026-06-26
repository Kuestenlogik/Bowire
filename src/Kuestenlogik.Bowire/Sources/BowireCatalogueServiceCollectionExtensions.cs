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
        // #309 — the override store applies the persisted UI override
        // (~/.bowire/catalogue-config.json) on first construction.
        // Singleton so the file is read at most once per process.
        services.TryAddSingleton(sp => new BowireCatalogueOverrideStore(
            sp.GetRequiredService<BowireCatalogueProviderAccessor>(),
            sp.GetService<ILoggerFactory>()));
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
        services.TryAddSingleton(sp => new BowireCatalogueOverrideStore(
            sp.GetRequiredService<BowireCatalogueProviderAccessor>(),
            sp.GetService<ILoggerFactory>()));
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
/// <remarks>
/// The accessor is mutable so #309's Settings → Catalogue providers
/// UI can hot-swap the active provider at runtime. The boot-time
/// provider (resolved from <c>appsettings.json</c>) is captured as
/// <see cref="DefaultProvider"/>; a UI-driven override replaces
/// <see cref="Provider"/> in place. Clearing the override restores
/// the boot-time provider.
/// </remarks>
public sealed class BowireCatalogueProviderAccessor
{
    private readonly object _lock = new();
    private IBowireCatalogueProvider? _provider;

    /// <summary>
    /// Construct an accessor wrapping the resolved provider (or
    /// <c>null</c> when no provider is configured).
    /// </summary>
    /// <param name="provider">The active provider, or <c>null</c>.</param>
    public BowireCatalogueProviderAccessor(IBowireCatalogueProvider? provider)
    {
        _provider = provider;
        DefaultProvider = provider;
    }

    /// <summary>
    /// The active catalogue provider, or <c>null</c> when no
    /// provider is configured. Consumers should treat <c>null</c> as
    /// "no catalogue" — symmetric with an empty fetch result.
    /// </summary>
    public IBowireCatalogueProvider? Provider
    {
        get { lock (_lock) return _provider; }
    }

    /// <summary>
    /// The boot-time provider resolved from <c>appsettings.json</c>.
    /// Captured at construction so a UI override can be cleared and
    /// the appsettings fallback restored without re-running
    /// <see cref="BowireCatalogueProviderRegistry.Resolve"/>.
    /// </summary>
    public IBowireCatalogueProvider? DefaultProvider { get; }

    /// <summary>
    /// Whether a UI-driven override is currently active. When
    /// <c>false</c>, <see cref="Provider"/> equals
    /// <see cref="DefaultProvider"/> (the appsettings fallback).
    /// </summary>
    public bool HasOverride { get; private set; }

    /// <summary>
    /// Replace the active provider — used by the Settings →
    /// Catalogue providers UI (#309) to hot-swap at runtime.
    /// Pass <c>null</c> to fall back to <see cref="DefaultProvider"/>.
    /// </summary>
    public void SetOverride(IBowireCatalogueProvider? overrideProvider)
    {
        lock (_lock)
        {
            if (overrideProvider is null)
            {
                _provider = DefaultProvider;
                HasOverride = false;
            }
            else
            {
                _provider = overrideProvider;
                HasOverride = true;
            }
        }
    }
}
